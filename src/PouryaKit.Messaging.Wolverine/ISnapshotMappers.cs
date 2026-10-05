namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// The Snapshot mappers the Host's Modules declared with <c>PublishSnapshot</c>, for the Outbox that builds, when the
/// Unit of Work commits, the Snapshots of the aggregates its Operation changed. <c>UsePouryaKitWolverine</c> registers
/// it, scoped; Feature code never calls it.
/// </summary>
public interface ISnapshotMappers
{
    /// <summary>
    /// True when the running Operation's Module declared a Snapshot for <paramref name="aggregateType"/> or a type it
    /// derives from.
    /// </summary>
    bool Declares(Type aggregateType);

    /// <summary>
    /// The Snapshot of <paramref name="aggregate"/> under each contract the running Operation's Module declared for it,
    /// or its Tombstone when <paramref name="removed"/>, each built by the contract's mapper; none when nothing is
    /// declared for it.
    /// </summary>
    IReadOnlyList<object> Map(object aggregate, bool removed);
}
