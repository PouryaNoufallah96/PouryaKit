using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.Sagas;
using PouryaKit.Modularity;

namespace ModularMonolith.Tests.Sagas;

/// <summary>
/// The Probes Module's Composition Root: Sagas whose steps a test sees refused. A test adds it beside the Host's Modules;
/// no published Host composes it, since one of its steps takes the Kit's Operation, which Wolverine cannot generate.
/// </summary>
public sealed class ProbesModule : IModuleDefinition
{
    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.PublishSnapshot<Shelf, ShelfSnapshotV1, ShelfSnapshotMapper>(
            "probes.shelf-snapshot.v1"
        );
        module.AddPouryaKitSaga<ContextProbeSaga>(
            "probes.context",
            "probes.context",
            saga => saga.Input<OrderPlacedV1>(placed => placed.OrderId)
        );
        module.AddPouryaKitSaga<SnapshotProbeSaga>(
            "probes.snapshot",
            "probes.snapshot",
            saga => saga.Input<ReferenceOpenedV1>(opened => opened.Reference)
        );
        module.AddPouryaKitSaga<CascadeProbeSaga>(
            "probes.cascade",
            "probes.cascade",
            saga => saga.Input<ReferenceOpenedV1>(opened => opened.Reference)
        );
    }
}

/// <summary>A saga whose starting step takes the Kit's Operation, which only service location reaches.</summary>
public sealed class ContextProbeSaga : PouryaKitSaga
{
    /// <summary>Starts the saga, reading the Operation.</summary>
    public static ContextProbeSaga Start(
        SagaInput<ContextProbeSaga, OrderPlacedV1> placed,
        Operation operation
    )
    {
        ArgumentNullException.ThrowIfNull(placed);
        ArgumentNullException.ThrowIfNull(operation);
        return new();
    }
}

/// <summary>A saga whose step emits a Snapshot its Module publishes.</summary>
public sealed class SnapshotProbeSaga : PouryaKitSaga
{
    /// <summary>Starts the saga, emitting a shelf's Snapshot.</summary>
    public Task StartOrHandle(SagaInput<SnapshotProbeSaga, ReferenceOpenedV1> opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return PublishAsync(new ShelfSnapshotV1(Guid.CreateVersion7(), Removed: false)).AsTask();
    }
}

/// <summary>A saga whose step returns an Integration Event, which Wolverine would cascade.</summary>
public sealed class CascadeProbeSaga : PouryaKitSaga
{
    /// <summary>Starts the saga, returning an order's fulfilment.</summary>
    public OrderFulfilledV1 StartOrHandle(SagaInput<CascadeProbeSaga, ReferenceOpenedV1> opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return new OrderFulfilledV1(Guid.CreateVersion7(), Payments: 0);
    }
}

internal sealed record ShelfId(Guid Value) : TypedId<Guid>(Value);

internal sealed class Shelf : AggregateRoot<ShelfId>;

internal sealed class ShelfSnapshotMapper : ISnapshotMapper<Shelf, ShelfSnapshotV1>
{
    public ShelfSnapshotV1 Map(Shelf aggregate) => new(aggregate.Id.Value, Removed: false);

    public ShelfSnapshotV1 Tombstone(Shelf aggregate) => new(aggregate.Id.Value, Removed: true);
}
