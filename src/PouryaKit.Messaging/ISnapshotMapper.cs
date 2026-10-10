using PouryaKit.Domain;

namespace PouryaKit.Messaging;

/// <summary>
/// Maps one <typeparamref name="TAggregate"/> to its Snapshot <typeparamref name="TSnapshot"/>, a plain record of the
/// Module's Contracts. The Kit calls it when the Unit of Work commits, once for every aggregate the Operation changed,
/// after every Domain Event Handler ran and persistence is prepared: the aggregate is in its final state and its
/// <see cref="IAggregateRoot.Version"/> is the version being committed, which the Snapshot carries, so a receiver
/// applies only a newer one. A mapper only reads: it runs as a step of the Unit of Work that only reads, so a command
/// it sends, and a write through a repository or the Unit of Work's connection, is refused, and the Operation fails if it
/// leaves a change in anything the Unit of Work saves, including complex collection contents, or pending Domain Events.
/// The comparison sees state at the read boundary; a transient change restored before it ends is not detected.
/// It may be <c>internal</c>. A Module declares it in its Composition Root with <c>PublishSnapshot</c>. The key of an
/// aggregate that publishes Snapshots is never used again after the aggregate is deleted outright: receivers keep its
/// Tombstone's version and would turn away a new aggregate's lower ones.
/// Its EF provider key must be a non-null Guid, int, long, string, byte array or DateTime. A DateTime key must use a
/// PostgreSQL timestamp mapping of precision at most 6, with every value aligned to that precision so reload preserves
/// its ticks; date mappings and values below the mapping's precision are refused before saving.
/// Nothing in the aggregate's mapped graph, its owned parts and the children it holds included, may have a value the
/// database generates when it is saved, such as a default value SQL, a computed column or a child's identity key: the
/// Snapshot is built before the save, so a commit of such an aggregate is refused, naming the property.
/// </summary>
public interface ISnapshotMapper<in TAggregate, out TSnapshot>
    where TAggregate : IAggregateRoot
    where TSnapshot : class
{
    /// <summary>The Snapshot of <paramref name="aggregate"/>, which exists.</summary>
    TSnapshot Map(TAggregate aggregate);

    /// <summary>
    /// The Tombstone of <paramref name="aggregate"/>, which the Operation removed, kept marked deleted or deleted
    /// outright: a Snapshot that says, in a field of its own, that the aggregate no longer exists.
    /// </summary>
    TSnapshot Tombstone(TAggregate aggregate);
}
