using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace PouryaKit.Messaging.Wolverine;

// The pre-built handler of a delayed command type, run once the message store finds it due. The queue it was stored on
// names its declaration, as a subscription's does, so one stored under a name nothing declares any more is set aside
// rather than run under another name's Receipts. Each attempt runs in a scope of its own, begun as a continuation of the
// Operation that scheduled it, in its Module; the envelope's id is the same in every delivery of one scheduled command,
// retries, recovery and replay included, so it keys the Receipt.
internal sealed class DelayedCommandDelivery(DelayedCommandDeclarations declarations) : MessageHandler
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
        var (operationContext, traceParent) = OperationContextHeaderExtensions.ContinuedOperationContext(envelope);

        await using var scope = context.Runtime.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(operationContext, declared.Module, traceParent);
        var result = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new RunDelayedCommand(declared, command, envelope.Id), cancellation);
        if (result.IsFailure)
        {
            throw new DelayedCommandFailedException(declared.Name, result.Errors);
        }
    }
}

// One delivery attempt of a delayed command, as the Operation of the scope the delivery opened: the command runs nested
// in it, so it joins the Unit of Work that records the Receipt, and goes through the whole pipeline, its authorizers
// and validator included.
internal sealed record RunDelayedCommand(DelayedCommand Declared, Command Command, Guid MessageId) : Command;

internal sealed class RunDelayedCommandHandler(IDispatcher dispatcher, IReceipts? receipts = null) : CommandHandler<RunDelayedCommand>
{
    public override async Task<Result> Handle(RunDelayedCommand command, CancellationToken cancellationToken)
    {
        // Recorded before the command runs, in the same transaction, as a subscription's Receipt is.
        if (receipts is not null && !await receipts.RecordAsync(command.Declared.Name, command.MessageId, cancellationToken))
        {
            return Result.Success();
        }

        return await dispatcher.SendAsync(command.Command, cancellationToken);
    }
}
