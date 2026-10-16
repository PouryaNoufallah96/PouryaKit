using PouryaKit.Domain;

namespace M2.Marten;

/// <summary>
/// A Stream: an aggregate folded from its events by Marten's <c>Apply</c> conventions. A behaviour applies the event to
/// itself and raises it; the Unit of Work drains the raised Domain Events and appends them to the stream.
/// </summary>
public abstract class StreamAggregate : IAggregateRoot
{
    private readonly List<DomainEvent> _domainEvents = [];

    /// <summary>The stream's id. Marten's stream ids are Guid or string; a Kit typed ID is a class, not one of them.</summary>
    public Guid Id { get; set; }

    /// <summary>The stream's version after the fold: the number of events it holds. Marten sets it.</summary>
    public long Version { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Raises <paramref name="domainEvent"/>, after those raised before it, for the Unit of Work to append.</summary>
    protected void Raise(DomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    void IAggregateRoot.ClearDomainEvents() => _domainEvents.Clear();
}
