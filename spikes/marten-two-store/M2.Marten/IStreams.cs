namespace M2.Marten;

/// <summary>
/// What a Stream Module's Feature code reads and starts Streams through, in its Operation's Marten Unit of Work. Feature
/// code never sees Marten's session, its store or its outbox.
/// </summary>
public interface IStreams
{
    /// <summary>Folds the stream <paramref name="id"/> of the Operation's tenant; null when it has no events.</summary>
    Task<T?> LoadAsync<T>(Guid id, CancellationToken cancellationToken)
        where T : StreamAggregate;

    /// <summary>Starts a new stream with <paramref name="stream"/>'s raised events when the Operation commits.</summary>
    void Start<T>(T stream)
        where T : StreamAggregate;
}
