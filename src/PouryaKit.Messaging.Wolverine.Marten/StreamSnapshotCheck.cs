using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Marten;

namespace PouryaKit.Messaging.Wolverine.Marten;

// A Snapshot declared for a Stream, or by a Stream Module of any aggregate type: a Stream's Unit of Work writes the
// Notifications and Integration Events of its events and builds no Snapshot (MartenStreamOutbox), so the declaration
// would never be sent, and nothing would say so. The Stream Modules, null for the application's own services, are those
// StreamModules records, which the registration hands over as it keys their Receipts. The declarations are read as
// SnapshotKeyTenancyCheck reads them, from a scope of the check's own with no Operation begun.
internal sealed class StreamSnapshotCheck(IReadOnlySet<Type?> streamModules) : ICompositionCheck
{
    private const string Fix =
        "Remove the PublishSnapshot declaration, and publish what the Stream's events tell other Modules as Notifications or Integration Events.";

    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var mappers = scope.ServiceProvider.GetRequiredService<ISnapshotMappers>();
        Type?[] owners = [null, .. composition.Modules];
        foreach (var module in owners)
        {
            var owner = module?.Name ?? "The application";
            foreach (var aggregate in mappers.DeclaredAggregates(module))
            {
                if (typeof(StreamAggregate).IsAssignableFrom(aggregate))
                {
                    yield return $"{owner} declares a Snapshot of {aggregate.FullName}, a Stream, which would never be sent: a Stream's Unit of Work publishes the Notifications and Integration Events of its events, and no Snapshot. {Fix}";
                }
                else if (streamModules.Contains(module))
                {
                    yield return $"{owner} declares a Snapshot of {aggregate.FullName}, which would never be sent: its Unit of Work is a Stream's, which publishes the Notifications and Integration Events of its events, and no Snapshot. {Fix}";
                }
            }
        }
    }
}
