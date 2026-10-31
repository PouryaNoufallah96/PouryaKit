using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace PouryaKit.Messaging.Wolverine;

// The pre-built handler of a delayed command type, run once the message store finds it due. The queue it was stored on
// names its declaration, as a subscription's does, so one stored under a name nothing declares any more is set aside
// rather than run under another name's Receipts. Each attempt runs in a scope of its own, in its Module: a delayed
// command's begun as a continuation of the Operation that scheduled it, a recurring command's as a new Operation of its
// declared system Actor, since no Operation caused it.
internal sealed partial class DelayedCommandDelivery(DelayedCommandDeclarations declarations)
    : MessageHandler
{
    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope!;
        var command = (Command)envelope.Message!;
        // A local queue's address is local://{name, lower-cased}/.
        var name = envelope.Destination!.Host;
        var declared =
            declarations.Named(name) is { } named && named.Command == command.GetType()
                ? named
                : throw new UnknownDelayedCommandException(name, command.GetType());
        // Checked before anything chooses the authority or the Receipt: a delivery stored as the other kind of the same
        // name, before its declaration changed, never runs under this one's.
        var occurrence = OccurrenceOf(envelope);
        if ((occurrence is not null) != (declared.Recurring is not null))
        {
            throw new UnknownDelayedCommandException(
                name,
                command.GetType(),
                storedAsOccurrence: occurrence is not null
            );
        }

        try
        {
            await RunAsync(context, declared, command, occurrence, cancellation);
        }
        catch (Exception failure) when (declared.Recurring is not null && IsSetAside(context))
        {
            // The failure of this attempt sets the occurrence aside as a dead letter, and the next occurrence it stored
            // rolled back with it, so the next one is stored in an Operation of its own unless one already waits. If
            // another Host stores one in between, or this Host dies before the dead letter is written and the occurrence
            // runs again, the occurrence-time Receipt keeps one of the chains. On the runtime's token, since this
            // attempt's may be the one its timeout cancelled. The dead letter keeps this attempt's failure: a failure
            // to store is logged beside it, and nothing then waits until a replay or a Host start stores the next one.
            try
            {
                await RecurringCommandStart.StoreFirstUnlessWaitingAsync(
                    context.Runtime.Services,
                    declared,
                    context.Runtime.Cancellation
                );
            }
            catch (Exception storeFailure)
            {
                LogNextOccurrenceNotStored(
                    context.Runtime.Services.GetRequiredService<ILogger<DelayedCommandDelivery>>(),
                    new AggregateException(storeFailure, failure),
                    declared.Name,
                    envelope.Id
                );
            }

            throw;
        }
    }

    [LoggerMessage(
        EventName = "NextOccurrenceNotStored",
        Level = LogLevel.Error,
        Message = "Storing the next occurrence of the recurring command {Name} failed (the first inner exception) as its occurrence {MessageId} was set aside as a dead letter with its own failure (the second); nothing waits until a replay of that dead letter or a Host start stores the next occurrence after now"
    )]
    private static partial void LogNextOccurrenceNotStored(
        ILogger logger,
        Exception exception,
        string name,
        Guid messageId
    );

    private static async Task RunAsync(
        MessageContext context,
        DelayedCommand declared,
        Command command,
        DateTimeOffset? occurrence,
        CancellationToken cancellation
    )
    {
        var envelope = context.Envelope!;
        // A recurring occurrence continues no Operation, so it starts a trace of its own too.
        var (operationContext, traceParent) = declared.Recurring is { } recurring
            ? (new OperationContext(Actor.System(recurring.SystemActor)), default(ActivityContext))
            : OperationContextHeaderExtensions.ContinuedOperationContext(envelope);

        await using var scope = context.Runtime.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(operationContext, declared.Module, traceParent);
        var result = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(
                new RunDelayedCommand(declared, command, envelope.Id, occurrence),
                cancellation
            );
        if (result.IsFailure)
        {
            throw new DelayedCommandFailedException(declared.Name, result.Errors);
        }
    }

    // Wolverine counts the attempt before it runs this handler and, when it throws, takes the slot of that number in the
    // Kit's retry policy: one stored retry per delay, then the error queue, which every later attempt goes to as well.
    private static bool IsSetAside(MessageContext context) =>
        context.Envelope!.Attempts
        > context
            .Runtime.Services.GetRequiredService<IOptions<PouryaKitMessagingOptions>>()
            .Value.RetryDelays.Length;

    private static DateTimeOffset? OccurrenceOf(Envelope envelope) =>
        envelope.Headers.TryGetValue(DelayedCommandHeaders.Occurrence, out var occurrence)
        && occurrence is not null
            ? DateTimeOffset.Parse(
                occurrence,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            )
            : null;
}

// One delivery attempt of a delayed command, as the Operation of the scope the delivery opened: the command runs nested
// in it, so it joins the Unit of Work that records the Receipt, and goes through the whole pipeline, its authorizers
// and validator included. A recurring command's attempt carries the occurrence it runs.
internal sealed record RunDelayedCommand(
    DelayedCommand Declared,
    Command Command,
    Guid MessageId,
    DateTimeOffset? Occurrence = null
) : Command;

internal sealed class RunDelayedCommandHandler(
    IDispatcher dispatcher,
    IServiceProvider scope,
    IOptionsMonitor<PouryaKitRecurringCommandOptions> recurringOptions,
    IReceipts? receipts = null
) : CommandHandler<RunDelayedCommand>
{
    public override async Task<Result> Handle(
        RunDelayedCommand command,
        CancellationToken cancellationToken
    )
    {
        Activity.Current?.SetTag(DeliverHandler.SubscriptionTag, command.Declared.Name);
        // Recorded before the command runs, in the same transaction, as a subscription's Receipt is. A delayed command's
        // Receipt is its message's; a recurring command's is its occurrence's, whichever stored delivery carries it.
        var receipt = command.Occurrence is { } occurrence
            ? ReceiptOf(occurrence)
            : command.MessageId;
        if (
            receipts is not null
            && !await receipts.RecordAsync(command.Declared.Name, receipt, cancellationToken)
        )
        {
            // A recurring occurrence already applied stores no next one: the delivery that applied it did.
            return Result.Success();
        }

        if (command.Declared.Recurring is { } recurring)
        {
            // The store fires on the system clock, so the next occurrence is compared with it, not with the Operation's
            // clock. One that is already past is skipped, never made up.
            var every = recurringOptions.Get(command.Declared.Name).Every;
            var next = Recurrence.After(
                Max(command.Occurrence!.Value, TimeProvider.System.GetUtcNow()),
                every
            );
            RecurringCommandStart.Store(scope, command.Declared, recurring.Occurrence(next), next);
        }

        return await dispatcher.SendAsync(command.Command, cancellationToken);
    }

    // The Receipt key of an occurrence: its time, in UTC ticks, in the key's first eight bytes.
    private static Guid ReceiptOf(DateTimeOffset occurrence)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64BigEndian(bytes, occurrence.UtcTicks);
        return new Guid(bytes, bigEndian: true);
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) =>
        left > right ? left : right;
}
