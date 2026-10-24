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
    /// derives from. Throws when a Snapshot of it is declared without a Module while the Operation runs in one, or with a
    /// Module while it runs in none, since such a declaration would never publish.
    /// </summary>
    bool Declares(Type aggregateType);

    /// <summary>
    /// Throws when one Snapshot the running Operation's Module declared covers aggregates of more than one mapped
    /// hierarchy. <paramref name="mappedAggregates"/> holds every aggregate type the Unit of Work maps, each with the root
    /// of its hierarchy: each root has keys of its own, which one Snapshot's identities and versions cannot tell apart.
    /// </summary>
    void CheckKeySpaces(IReadOnlyDictionary<Type, Type> mappedAggregates);

    /// <summary>
    /// The aggregate type of every Snapshot <paramref name="module"/> declared, or the application when it is null,
    /// whatever Operation runs: for a composition check that judges the keys those aggregates are mapped with.
    /// </summary>
    IReadOnlyList<Type> DeclaredAggregates(Type? module);

    /// <summary>
    /// The Snapshot of <paramref name="aggregate"/> under each contract the running Operation's Module declared for it,
    /// or its Tombstone when <paramref name="removed"/>, each built by the contract's mapper; none when nothing is
    /// declared for it.
    /// </summary>
    IReadOnlyList<object> Map(object aggregate, bool removed);
}
