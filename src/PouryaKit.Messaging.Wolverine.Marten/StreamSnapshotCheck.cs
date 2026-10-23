using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Marten;

namespace PouryaKit.Messaging.Wolverine.Marten;

// A Snapshot declared for a Stream: a Stream's Unit of Work writes the Notifications and Integration Events of its events
// and builds no Snapshot (MartenStreamOutbox), so the declaration would never be sent, and nothing would say so. The
// declarations are read as SnapshotKeyTenancyCheck reads them, from a scope of the check's own with no Operation begun.
internal sealed class StreamSnapshotCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var mappers = scope.ServiceProvider.GetRequiredService<ISnapshotMappers>();
        Type?[] owners = [null, .. composition.Modules];
        foreach (var module in owners)
        {
            foreach (var stream in mappers.DeclaredAggregates(module).Where(typeof(StreamAggregate).IsAssignableFrom))
            {
                yield return $"{module?.Name ?? "The application"} declares a Snapshot of {stream.FullName}, a Stream, which would never be sent: a Stream's Unit of Work publishes the Notifications and Integration Events of its events, and no Snapshot. Remove the PublishSnapshot declaration, and publish what the Stream's events tell other Modules as Notifications or Integration Events.";
            }
        }
    }
}
