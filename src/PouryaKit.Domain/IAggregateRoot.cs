namespace PouryaKit.Domain;

/// <summary>
/// What the Unit of Work reads from every aggregate, whatever its ID type. Feature code raises
/// Domain Events through the aggregate and never clears them.
/// </summary>
public interface IAggregateRoot
{
    long Version { get; }

    IReadOnlyList<DomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
