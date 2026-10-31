namespace PouryaKit.AI.Postgres;

/// <summary>
/// Text stored by its embedding in a vector collection of the package's schema, and found again by meaning: the
/// semantic search an agent's tool or a query runs. Every row belongs to the tenant of the Operation that stored it, and
/// every call reads and writes the rows of its own Operation's tenant only. A Module registers it with
/// <c>AddPouryaKitVectorStore</c>, the Host its embedding generator with <c>AddPouryaKitEmbeddings</c>, and a migration
/// of the Module creates each collection with <see cref="VectorCollectionTable"/>.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Embeds <paramref name="content"/> and stores it under <paramref name="key"/> in <paramref name="collection"/>,
    /// replacing what the key held, on the active Unit of Work's transaction: a rollback stores nothing. Use it only
    /// from a command's handler or a Domain Event Handler: it throws <see cref="InvalidOperationException"/> in code
    /// that only reads, where no Unit of Work transaction is active, and when the collection has no table.
    /// </summary>
    Task StoreAsync(
        string collection,
        string key,
        string content,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Removes what <paramref name="key"/> holds in <paramref name="collection"/>, if anything, on the active Unit of
    /// Work's transaction, where and as <see cref="StoreAsync"/> writes.
    /// </summary>
    Task RemoveAsync(string collection, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Embeds <paramref name="query"/> and returns at most <paramref name="top"/> of the texts stored in
    /// <paramref name="collection"/>, nearest first by cosine distance, from the committed rows and those of the
    /// Operation's own Unit of Work. An exact search over the tenant's rows. Throws
    /// <see cref="InvalidOperationException"/> when the collection has no table.
    /// </summary>
    Task<IReadOnlyList<VectorMatch>> SearchAsync(
        string collection,
        string query,
        int top,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// A text <see cref="IVectorStore.SearchAsync"/> found: its <paramref name="Key"/>, the <paramref name="Content"/>
/// stored under it, and its cosine <paramref name="Distance"/> from the query, from 0 for the same direction to 2.
/// </summary>
public sealed record VectorMatch(string Key, string Content, double Distance);
