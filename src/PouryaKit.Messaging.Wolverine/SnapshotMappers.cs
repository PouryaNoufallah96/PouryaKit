using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

// Runs the mappers in the committing Operation's scope, only those its own Module declared: a declaration for a base
// type or an interface never reaches another Module's aggregates. A mapper and its aggregate may be internal: they are
// only ever named through reflection, never in generated code.
internal sealed class SnapshotMappers(Subscriptions subscriptions, Operation operation, IServiceProvider scope) : ISnapshotMappers
{
    private static readonly ConcurrentDictionary<SnapshotPublication, Mapping> Mappings = new();

    private IEnumerable<SnapshotPublication> Declared =>
        subscriptions.Snapshots.Where(snapshot => subscriptions.Publications[snapshot.Snapshot].Module == operation.Module);

    public bool Declares(Type aggregateType) => Declared.Any(snapshot => snapshot.Aggregate.IsAssignableFrom(aggregateType));

    public IReadOnlyList<object> Map(object aggregate, bool removed)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return
        [
            .. Declared
                .Where(snapshot => snapshot.Aggregate.IsInstanceOfType(aggregate))
                .Select(snapshot =>
                    Mappings
                        .GetOrAdd(
                            snapshot,
                            declared =>
                                (Mapping)
                                    Activator.CreateInstance(typeof(Mapping<,>).MakeGenericType(declared.Aggregate, declared.Snapshot))!
                        )
                        .Map(scope.GetRequiredService(snapshot.Mapper), aggregate, removed)
                ),
        ];
    }

    private abstract class Mapping
    {
        public abstract object Map(object mapper, object aggregate, bool removed);
    }

    private sealed class Mapping<TAggregate, TSnapshot> : Mapping
        where TAggregate : IAggregateRoot
        where TSnapshot : class
    {
        public override object Map(object mapper, object aggregate, bool removed) =>
            removed
                ? ((ISnapshotMapper<TAggregate, TSnapshot>)mapper).Tombstone((TAggregate)aggregate)
                : ((ISnapshotMapper<TAggregate, TSnapshot>)mapper).Map((TAggregate)aggregate);
    }
}
