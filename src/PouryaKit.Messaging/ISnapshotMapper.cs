using PouryaKit.Domain;

namespace PouryaKit.Messaging;

/// <summary>
/// Maps one <typeparamref name="TAggregate"/> to its Snapshot <typeparamref name="TSnapshot"/>, a plain record of the
/// Module's Contracts. The Kit calls it when the Unit of Work commits, once for every aggregate the Operation changed,
/// after every Domain Event Handler ran and persistence is prepared: the aggregate is in its final state and its
/// <see cref="IAggregateRoot.Version"/> is the version being committed, which the Snapshot carries, so a receiver
/// applies only a newer one. A mapper only reads: the Operation fails if it changes anything the Unit of Work saves.
/// It may be <c>internal</c>. A Module declares it in its Composition Root with <c>PublishSnapshot</c>.
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
