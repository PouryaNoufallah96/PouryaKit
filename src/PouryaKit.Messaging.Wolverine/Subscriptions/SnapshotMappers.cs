using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

// Runs the mappers in the committing Operation's scope, only those its own Module declared, each as a step that only
// reads: a declaration for a base type or an interface never reaches another Module's aggregates. A mapper and its aggregate may be internal: they are
// only ever named through reflection, never in generated code.
internal sealed class SnapshotMappers(
    Subscriptions subscriptions,
    Operation operation,
    IServiceProvider scope
) : ISnapshotMappers
{
    private static readonly ConcurrentDictionary<SnapshotPublication, Mapping> Mappings = new();

    private IEnumerable<SnapshotPublication> Declared => DeclaredBy(operation.Module);

    public IReadOnlyList<Type> DeclaredAggregates(Type? module) =>
        [.. DeclaredBy(module).Select(snapshot => snapshot.Aggregate)];

    private IEnumerable<SnapshotPublication> DeclaredBy(Type? module) =>
        subscriptions.Snapshots.Where(snapshot =>
            subscriptions.Publications[snapshot.Snapshot].Module == module
        );

    public bool Declares(Type aggregateType)
    {
        // A declaration made without its Module in a Host of Modules, or with one in an application without Modules,
        // would match no Operation and publish nothing, silently.
        if (
            subscriptions
                .Snapshots.Where(snapshot => snapshot.Aggregate.IsAssignableFrom(aggregateType))
                .FirstOrDefault(snapshot =>
                    (subscriptions.Publications[snapshot.Snapshot].Module is null)
                    != (operation.Module is null)
                ) is
            { } misdeclared
        )
        {
            throw new InvalidOperationException(
                operation.Module is { } module
                    ? $"The Snapshot {misdeclared.Snapshot.FullName} of {misdeclared.Aggregate.FullName} is declared without a Module, but {aggregateType.Name} changed in an Operation of {module.Name}. Declare it with PublishSnapshot(wireName) in that Module's Composition Root."
                    : $"The Snapshot {misdeclared.Snapshot.FullName} of {misdeclared.Aggregate.FullName} is declared by the Module {subscriptions.Publications[misdeclared.Snapshot].Module!.Name}, but {aggregateType.Name} changed in an application without Modules. Declare it with PublishSnapshot(wireName) and no Module."
            );
        }

        return Declared.Any(snapshot => snapshot.Aggregate.IsAssignableFrom(aggregateType));
    }

    public void CheckKeySpaces(IReadOnlyDictionary<Type, Type> mappedAggregates)
    {
        ArgumentNullException.ThrowIfNull(mappedAggregates);
        foreach (var snapshot in Declared)
        {
            List<Type> roots =
            [
                .. mappedAggregates
                    .Where(mapped => snapshot.Aggregate.IsAssignableFrom(mapped.Key))
                    .Select(mapped => mapped.Value)
                    .Distinct(),
            ];
            if (roots.Count > 1)
            {
                throw new InvalidOperationException(
                    $"The Snapshot {snapshot.Snapshot.FullName} is declared for {snapshot.Aggregate.FullName}, which covers aggregates of the separately mapped hierarchies of {string.Join(", ", roots.Select(root => root.FullName))}. Their keys are their own, so their Snapshots would share identities and versions, and a receiver would turn one aggregate's away for another's. Declare a Snapshot for each aggregate, or for a type of one mapped hierarchy."
                );
            }
        }
    }

    public IReadOnlyList<object> Map(object aggregate, bool removed)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return
        [
            .. Declared
                .Where(snapshot => snapshot.Aggregate.IsInstanceOfType(aggregate))
                .Select(snapshot =>
                {
                    var mapping = Mappings.GetOrAdd(
                        snapshot,
                        declared =>
                            (Mapping)
                                Activator.CreateInstance(
                                    typeof(Mapping<,>).MakeGenericType(
                                        declared.Aggregate,
                                        declared.Snapshot
                                    )
                                )!
                    );
                    // SnapshotStep compares the prepared batch once; this scope guards each mapper's write entry
                    // points from its construction on, without copying the whole tracker again for each aggregate.
                    using var reading = operation.OnlyReading(
                        "a Snapshot mapper",
                        compareTrackedState: false
                    );
                    return mapping.Map(
                        scope.GetRequiredService(snapshot.Mapper),
                        aggregate,
                        removed
                    );
                }),
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
