namespace PouryaKit.Domain;

/// <summary>
/// What the Unit of Work reads from every aggregate, whatever its ID type. Feature code raises
/// Domain Events through the aggregate and never clears them.
/// </summary>
public interface IAggregateRoot
{
    /// <summary>
    /// The committed version, which the Unit of Work advances for each Operation that changed the aggregate.
    /// </summary>
    long Version { get; }

    /// <summary>
    /// The Domain Events the aggregate raised that the Unit of Work has not taken yet, in the order raised.
    /// </summary>
    IReadOnlyList<DomainEvent> DomainEvents { get; }

    /// <summary>Forgets the raised Domain Events once the Unit of Work has taken them.</summary>
    void ClearDomainEvents();
}
