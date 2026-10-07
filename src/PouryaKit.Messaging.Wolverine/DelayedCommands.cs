using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// Scheduling is allowed only where the Operation may change state, so a delayed command commits with the change that
// caused it, and only for a command its own Module declared, so it runs in that Module, on a durable queue of its own.
internal sealed class DelayedCommands(DelayedCommandDeclarations declarations, Operation operation, IServiceProvider scope)
    : IDelayedCommands
{
    public void Schedule(Command command, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(command);
        var type = command.GetType();
        if (!operation.MayChangeState)
        {
            throw new InvalidOperationException(
                $"Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, scheduled {type.Name}. Schedule a command from a command's handler, a Domain Event Handler or a Notification Handler."
            );
        }

        if (declarations.Of(type) is not { } declared || declared.Module != operation.Module)
        {
            throw new InvalidOperationException(
                $"{operation.Module?.Name ?? "The application"} does not declare that it schedules {type.FullName}. Declare it, with its name, in the Composition Root with AddPouryaKitDelayedCommand<{type.Name}>(name, module)."
            );
        }

        if (declared.Recurring is not null)
        {
            throw new InvalidOperationException(
                $"{type.FullName} is the recurring command {declared.Name}, whose occurrences the Kit stores itself, one at a time; scheduling one more would run it outside its cadence."
            );
        }

        (
            scope.GetService<IDelayedCommandOutbox>()
            ?? throw new InvalidOperationException(
                $"{type.Name} was scheduled, but nothing writes delayed commands to an Outbox. Call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore on the Host."
            )
        ).Add(command, at);
    }
}
