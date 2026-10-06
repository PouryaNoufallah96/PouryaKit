namespace PouryaKit.Domain;

/// <summary>The root of an aggregate: records the Domain Events it raises and exposes its version.</summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot
    where TId : notnull
{
    private readonly List<DomainEvent> _domainEvents = [];

    /// <summary>
    /// The committed version: 0 until first saved, 1 once saved, and one more for each later Operation that changed
    /// the aggregate, removing it included. Only the Unit of Work advances it, through the persistence mapping;
    /// nothing in the domain sets it, and raising a Domain Event does not move it. A change to anything the aggregate
    /// holds in a navigation, an owned type or a child entity, changed, added or removed, changes the aggregate; a child
    /// moved from one aggregate to another changes both. Changing a part of an aggregate the Operation did not load
    /// throws when the Operation commits. A child the aggregate reaches by no navigation is not part of it.
    /// </summary>
    public long Version { get; private set; }

    /// <summary>The Domain Events raised and not yet taken by the Unit of Work, in the order raised.</summary>
    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void Raise(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    void IAggregateRoot.ClearDomainEvents() => _domainEvents.Clear();
}
