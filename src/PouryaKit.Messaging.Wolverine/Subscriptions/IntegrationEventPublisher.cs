using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

// Publishing is allowed only where G.2 puts it, in a Notification Handler's delivery, which always runs in a Unit of
// Work, where the Operation may change state, and only with the identity of the Domain Event delivered, so no
// Integration Event gets an identity a redelivery would not repeat. Each receiver keeps one Receipt per identity, so a
// second Integration Event of one type with the same identity would be acknowledged and never applied.
internal sealed class IntegrationEventPublisher(Subscriptions subscriptions, Operation operation, IServiceProvider scope)
    : IIntegrationEventPublisher
{
    private readonly HashSet<Type> _published = [];

    /// <summary>The Domain Event whose Notification Handler runs in this scope; null in any other scope.</summary>
    public DomainEvent? Following { get; set; }

    public void Publish<TIntegrationEvent>(Guid domainEventId, TIntegrationEvent integrationEvent)
        where TIntegrationEvent : class
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var type = integrationEvent.GetType();
        if (!operation.MayChangeState)
        {
            throw new InvalidOperationException(
                $"Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, published {type.Name}. Publish an Integration Event from a Notification Handler."
            );
        }

        if (Following is not { } domainEvent)
        {
            throw new InvalidOperationException(
                $"{type.Name} was published outside a Notification Handler's delivery. An Integration Event is published by a Notification Handler, so it is written to the Outbox with that delivery's commit."
            );
        }

        if (domainEventId != domainEvent.Id)
        {
            throw new InvalidOperationException(
                $"{type.Name} was published with the identity {domainEventId}, but it follows from the {domainEvent.GetType().Name} {domainEvent.Id} being delivered. Publish it with the Domain Event's Id."
            );
        }

        if (subscriptions.IsSnapshot(type))
        {
            throw new InvalidOperationException(
                $"{type.FullName} is a Snapshot. The Kit publishes it whenever the Unit of Work commits a change of its aggregate; nothing publishes it by hand."
            );
        }

        if (!subscriptions.Publications.TryGetValue(type, out var publication) || publication.Module != operation.Module)
        {
            throw new InvalidOperationException(
                $"{operation.Module?.Name ?? "The application"} does not declare that it publishes {type.FullName}. Declare it, with its wire name, in the Composition Root with Publish<{type.Name}>(wireName)."
            );
        }

        if (!_published.Add(type))
        {
            throw new InvalidOperationException(
                $"{type.Name} was published twice for the {domainEvent.GetType().Name} {domainEvent.Id}. One Domain Event yields at most one Integration Event of each type: both would carry its identity, and every receiver would apply only the first."
            );
        }

        scope.GetRequiredService<IIntegrationEventOutbox>().Add(domainEventId, integrationEvent);
    }
}
