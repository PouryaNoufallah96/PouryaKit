using PouryaKit.Domain;

namespace PouryaKit.Messaging;

/// <summary>
/// Maps one <typeparamref name="TAggregate"/> to its Snapshot <typeparamref name="TSnapshot"/>, a plain record of the
/// Module's Contracts. The Kit calls it when the Unit of Work commits, once for every aggregate the Operation changed,
/// after every Domain Event Handler ran and persistence is prepared: the aggregate is in its final state and its
/// <see cref="IAggregateRoot.Version"/> is the version being committed, which the Snapshot carries, so a receiver
/// applies only a newer one. A mapper only reads: it runs as a step of the Unit of Work that only reads, so a command
/// it sends, and a write through a repository or the Unit of Work's connection, is refused, and the Operation fails if it
/// changes anything the Unit of Work saves, except a change inside an EF Core complex collection, which is not seen.
/// It may be <c>internal</c>. A Module declares it in its Composition Root with <c>PublishSnapshot</c>. The key of an
/// aggregate that publishes Snapshots is never used again after the aggregate is deleted outright: receivers keep its
/// Tombstone's version and would turn away a new aggregate's lower ones.
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
