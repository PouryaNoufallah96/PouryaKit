using PouryaKit.Domain;

namespace PouryaKit.Messaging;

/// <summary>
/// A reaction to one <typeparamref name="TDomainEvent"/> after the Operation that raised it committed: where a Module
/// reaches outside its own data, such as sending something. It runs in a scope of its own, as a continuation of the
/// raising Operation, and is retried and recorded apart from every other Notification Handler of the same Domain Event.
/// A class handles one Domain Event type, and runs only for a Domain Event of exactly that type. It returns a Result and
/// never saves or commits; a failed Result, like an exception, is retried. Both may be <c>internal</c>. A reaction that
/// must commit with the change that raised the Domain Event is a Domain Event Handler instead.
/// </summary>
public interface INotificationHandler<TDomainEvent>
    where TDomainEvent : DomainEvent
{
    /// <summary>Reacts to the committed <paramref name="domainEvent"/> and returns the reaction's Result.</summary>
    Task<Result> Handle(TDomainEvent domainEvent, CancellationToken cancellationToken);
}
