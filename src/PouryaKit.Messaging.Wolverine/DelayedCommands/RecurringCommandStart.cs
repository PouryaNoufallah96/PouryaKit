using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.Runtime;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine;

// Runs once every hosted service has started, Wolverine and DurableQueuesCheck included: stores the first occurrence of each
// recurring command that has none waiting in the message store, as an Operation of its system Actor, in its Module, so
// its Unit of Work writes it to the Outbox as it writes every other. Two Hosts that start together may both store one;
// the Receipt of the occurrence they then share makes one of them apply nothing and end. The attempt that sets an
// occurrence aside as a dead letter stores one the same way (DelayedCommandDelivery). A first occurrence that fails to
// store fails the start once every hosted service has started, and the Host rethrows it without stopping them, so the
// Host is stopped first; a stop that fails is kept beside the failure, which comes first. A cancelled start is its
// caller's to end.
internal sealed class RecurringCommandStart(DelayedCommandDeclarations declarations, IServiceProvider services)
    : IHostedLifecycleService
{
    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var declared in declarations.All.Where(declared => declared.Recurring is not null))
            {
                await StoreFirstUnlessWaitingAsync(services, declared, cancellationToken);
            }
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await services.GetRequiredService<IHost>().StopAsync(CancellationToken.None);
            }
            catch (Exception stop)
            {
                throw new AggregateException(failure, stop);
            }

            throw;
        }
    }

    // Stores the first occurrence after now of declared unless one waits in the message store.
    internal static async Task StoreFirstUnlessWaitingAsync(IServiceProvider services, DelayedCommand declared, CancellationToken cancellationToken)
    {
        var scheduled = services.GetRequiredService<IWolverineRuntime>().Storage.ScheduledMessages;
        // Waiting to come due, or to be retried, on this declaration's queue: one stored under a name since retired is
        // set aside when due, so it does not hold back the chain of the new name. A page size of 0 reads all.
        var waiting = await scheduled.QueryAsync(
            new ScheduledMessageQuery { MessageType = declared.Command.ToMessageTypeName(), PageSize = 0 },
            cancellationToken
        );
        if (waiting.Messages.Any(message => string.Equals(message.Destination, $"local://{declared.Name}/", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System(declared.Recurring!.SystemActor)), declared.Module);
        var result = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new StartRecurringCommand(declared), cancellationToken);
        if (result.IsFailure)
        {
            throw new DelayedCommandFailedException(declared.Name, result.Errors);
        }
    }

    // Adds the occurrence due at at to what the scope's Unit of Work writes to the Outbox.
    internal static void Store(IServiceProvider scope, DelayedCommand declared, Command occurrence, DateTimeOffset at)
    {
        if (occurrence.GetType() != declared.Command)
        {
            throw new InvalidOperationException(
                $"The recurring command {declared.Name} built a {occurrence.GetType().FullName} for its occurrence at {at:O}, not the {declared.Command.FullName} it declares."
            );
        }

        (
            scope.GetService<IDelayedCommandOutbox>()
            ?? throw new InvalidOperationException(
                $"The recurring command {declared.Name} runs, but nothing writes delayed commands to an Outbox. Call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore on the Host."
            )
        ).AddOccurrence(occurrence, at);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// The Operation that stores a recurring command's first occurrence: the first one after now, on the system clock, the
// one the message store compares it with, so also after an occurrence set aside, which came due on that clock.
internal sealed record StartRecurringCommand(DelayedCommand Declared) : Command;

internal sealed class StartRecurringCommandHandler(IServiceProvider scope, IOptionsMonitor<PouryaKitRecurringCommandOptions> options)
    : CommandHandler<StartRecurringCommand>
{
    public override Task<Result> Handle(StartRecurringCommand command, CancellationToken cancellationToken)
    {
        var declared = command.Declared;
        var first = Recurrence.After(TimeProvider.System.GetUtcNow(), options.Get(declared.Name).Every);
        RecurringCommandStart.Store(scope, declared, declared.Recurring!.Occurrence(first), first);
        return Task.FromResult(Result.Success());
    }
}
