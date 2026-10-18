using PouryaKit.Domain;

namespace PouryaKit.Persistence.Marten;

/// <summary>
/// The Streams of the Operation's Module, as its Feature code reads and starts them: a command's handler or a Domain
/// Event Handler changes a Stream it loaded or started here, and the Module's Marten Unit of Work appends what it raised
/// when the Operation commits; a query reads them too. Each Stream is the Operation's tenant's: one id is a stream of its
/// own under each tenant, and an Operation with no tenant reads and writes Marten's tenantless streams. Feature code never
/// sees Marten's session, store or outbox.
/// </summary>
public interface IStreams
{
    /// <summary>
    /// The Stream of the identity <paramref name="id"/> wraps, folded from its events, or null when it has none; the same
    /// instance when the Operation already loaded or started it. A step that only reads, such as a query, may load one but
    /// must leave it as it was: a change there is refused when the step ends.
    /// </summary>
    Task<TStream?> LoadAsync<TStream>(TypedId<Guid> id, CancellationToken cancellationToken)
        where TStream : StreamAggregate;

    /// <summary>
    /// Starts <paramref name="stream"/>, whose events append as a new stream of its <see cref="StreamAggregate.Id"/> when
    /// the Operation commits; a stream of that identity already there fails the commit as a concurrent change. Throws
    /// <see cref="InvalidOperationException"/> in code that only reads, and while no Unit of Work is active.
    /// </summary>
    void Start<TStream>(TStream stream)
        where TStream : StreamAggregate;
}
