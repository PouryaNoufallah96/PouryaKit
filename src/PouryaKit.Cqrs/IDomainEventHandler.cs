using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// A reaction to one <typeparamref name="TDomainEvent"/> inside its Module, run by the Unit of Work before it commits,
/// in the same transaction as the change that raised it. A class handles one Domain Event type, and runs only for a
/// Domain Event of exactly that type, never for one derived from it; a Domain Event may have several handlers, run one
/// at a time in the ordinal order of their full type names, or none. It returns a Result and
/// never saves or commits; a failed Result rolls the whole Operation back. It may change other aggregates, which may
/// raise further Domain Events, and may send nested commands through <see cref="IDispatcher"/>, which join the
/// Operation's Unit of Work.
/// </summary>
public interface IDomainEventHandler<TDomainEvent>
    where TDomainEvent : DomainEvent
{
    /// <summary>Reacts to <paramref name="domainEvent"/> and returns the reaction's Result.</summary>
    Task<Result> Handle(TDomainEvent domainEvent, CancellationToken cancellationToken);
}
