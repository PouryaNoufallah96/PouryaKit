using PouryaKit.Domain;

namespace PouryaKit.Persistence.Marten;

/// <summary>
/// The base of a Stream: an aggregate whose stored state is the events it appended, folded by Marten through its
/// <c>Create</c> and <c>Apply</c> methods. Declare it <c>internal</c> and those methods <c>public</c>, which is all Marten's
/// source generator dispatches to (ADR 0011). A behaviour applies its event to the Stream and raises it with
/// <see cref="Raise"/>; the Marten Unit of Work drains the raised Domain Events and appends them to the stream when the
/// Operation commits.
/// </summary>
public abstract class StreamAggregate : IAggregateRoot
{
    private readonly List<DomainEvent> _domainEvents = [];

    /// <summary>
    /// The stream's identity, the Guid a typed ID of the Stream wraps: Marten's streams take a Guid, never a typed ID.
    /// The fold sets it, so an <c>Apply</c> of the Stream's first event sets it too; the Unit of Work appends to the
    /// identity it read or started, whatever it is set to later.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>The number of events the stream held when it was read, which Marten's fold sets: 0 for a new Stream.</summary>
    public long Version { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<DomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Raises <paramref name="domainEvent"/>, after those raised before it, for the Unit of Work to append.</summary>
    protected void Raise(DomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    void IAggregateRoot.ClearDomainEvents() => _domainEvents.Clear();
}
