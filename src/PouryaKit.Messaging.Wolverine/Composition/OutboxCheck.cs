using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// A Notification, an Integration Event, a Snapshot or a delayed command is durable only when an Outbox writes it in the
// Unit of Work's commit; with none registered, every Operation would commit and nothing would be delivered.
internal sealed class OutboxCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var subscriptions = services.GetService<Subscriptions>();
        var declared =
            subscriptions is not null && (subscriptions.All.Any() || subscriptions.Publications.Count > 0)
            || services.GetService<DelayedCommandDeclarations>()?.All.Any() == true;
        return !declared || services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(INotificationOutbox))
            ? []
            : [
                "Notification Handlers, Integration Events, Snapshots or delayed commands are declared, but nothing writes them to an Outbox, so every Operation would commit and none would be delivered. Call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore on the Host.",
            ];
    }
}
