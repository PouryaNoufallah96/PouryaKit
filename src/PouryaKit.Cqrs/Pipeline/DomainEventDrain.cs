using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The Domain Event drain of the Unit of Work's commit sequence: runs the Domain Event Handlers of every Domain Event the Operation's
/// aggregates raised, inside the Unit of Work, until none remain. An <see cref="IUnitOfWork"/> calls it after the
/// outermost command succeeded and before it saves; Feature code never calls it.
/// </summary>
public sealed class DomainEventDrain
{
    /// <summary>
    /// The most rounds one drain runs. Round 1 handles the Domain Events the command raised; each further round handles
    /// those raised by the handlers of the round before.
    /// </summary>
    public const int MaxDepth = 10;

    private static readonly ConcurrentDictionary<Type, Invoker> Invokers = new();

    private readonly IServiceProvider _services;
    private readonly Operation _operation;

    internal DomainEventDrain(IServiceProvider services, Operation operation)
    {
        _services = services;
        _operation = operation;
    }

    /// <summary>
    /// Takes and clears the Domain Events of every aggregate <paramref name="aggregates"/> lists, then runs each one's
    /// handlers one at a time: aggregates in the order listed, each one's Domain Events in the order raised. Repeats,
    /// listing the aggregates again, until no aggregate holds a Domain Event. Stops as soon as the Operation is
    /// rollback-only: a failed handler Result marks it with the handler's Errors, and so does a failed nested command.
    /// The caller checks <see cref="Operation.IsRollbackOnly"/> afterwards and commits only when it is false. Returns
    /// the Domain Events handled, in the order handled. Throws an <see cref="InvalidOperationException"/> when
    /// Domain Events remain after <see cref="MaxDepth"/> rounds: a cycle of reactions is a bug, not a business failure.
    /// </summary>
    public async Task<IReadOnlyList<DomainEvent>> RunAsync(
        Func<IEnumerable<IAggregateRoot>> aggregates,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(aggregates);

        List<DomainEvent> handled = [];
        for (var depth = 1; ; depth++)
        {
            List<DomainEvent> raised = [];
            foreach (var aggregate in aggregates())
            {
                raised.AddRange(aggregate.DomainEvents);
                aggregate.ClearDomainEvents();
            }

            if (raised.Count == 0)
            {
                return handled;
            }

            if (depth > MaxDepth)
            {
                throw new InvalidOperationException(
                    $"Domain Event Handlers still raised {string.Join(", ", raised.Select(e => e.GetType().Name).Distinct())} after {MaxDepth} rounds. Domain Event Handlers that keep raising each other's Domain Events are a cycle; nothing was committed."
                );
            }

            foreach (var domainEvent in raised)
            {
                var invoker = Invokers.GetOrAdd(
                    domainEvent.GetType(),
                    static type =>
                        (Invoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(type))!
                );
                await invoker.Invoke(this, domainEvent, cancellationToken);
                if (_operation.IsRollbackOnly)
                {
                    return handled;
                }

                handled.Add(domainEvent);
            }
        }
    }

    private abstract class Invoker
    {
        public abstract Task Invoke(
            DomainEventDrain drain,
            DomainEvent domainEvent,
            CancellationToken cancellationToken
        );
    }

    private sealed class Invoker<TDomainEvent> : Invoker
        where TDomainEvent : DomainEvent
    {
        // Ordered by name, so the order is stated rather than whatever order the handlers happened to be registered in.
        public override async Task Invoke(
            DomainEventDrain drain,
            DomainEvent domainEvent,
            CancellationToken cancellationToken
        )
        {
            var handlers = drain
                ._services.GetServices<IDomainEventHandler<TDomainEvent>>()
                .OrderBy(handler => handler.GetType().FullName, StringComparer.Ordinal);
            foreach (var handler in handlers)
            {
                var result = await handler.Handle((TDomainEvent)domainEvent, cancellationToken);
                if (result.IsFailure)
                {
                    drain._operation.MarkRollbackOnly(result.Errors);
                }

                if (drain._operation.IsRollbackOnly)
                {
                    return;
                }
            }
        }
    }
}
