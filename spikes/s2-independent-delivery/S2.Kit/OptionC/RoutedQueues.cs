using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports.Tcp;

namespace S2.Kit.OptionC;

/// <summary>
/// Option C: one durable local queue per subscription, filled by Wolverine's own routing (one route per subscription,
/// so one envelope per subscription), and one pre-built Kit handler per message type, registered with
/// <c>AddMessageHandler</c> so that no code is generated. The handler picks the subscription from the endpoint the
/// envelope was delivered to.
/// </summary>
/// <param name="firstTcpPort">
/// Null (the option as decided): local queues. A port (observed variant, standing in for a broker): each subscription
/// gets its own TCP listener with a durable inbox, starting at this port, and the route goes through the durable outbox.
/// </param>
public sealed class RoutedQueues(int? firstTcpPort = null) : IDeliveryOption
{
    public void Configure(KitMessaging messaging, IServiceCollection services, WolverineOptions opts)
    {
        var ports = new Dictionary<int, Subscription>();
        var port = firstTcpPort;
        foreach (var s in messaging.Subscriptions)
        {
            if (port is not { } p) opts.PublishMessage(s.MessageType).ToLocalQueue(s.Name);
            else
            {
                opts.ListenAtPort(p).UseDurableInbox();
                opts.PublishMessage(s.MessageType).ToPort(p).UseDurableOutbox();
                ports[p] = s;
                port = p + 1;
            }
        }

        foreach (var messageType in messaging.Subscriptions.Select(s => s.MessageType).Distinct())
            opts.AddMessageHandler(messageType, new RoutedHandler(messaging, ports));
    }

    public ValueTask PublishAsync(IMessageBus outbox, object message) => outbox.PublishAsync(message);
}

/// <summary>A local queue's URI is <c>local://{queue name}/</c>; the queue name is the subscription name, lower-cased.</summary>
public sealed class RoutedHandler(KitMessaging messaging, Dictionary<int, Subscription> ports) : MessageHandler
{
    public override Task HandleAsync(MessageContext context, CancellationToken ct)
    {
        var destination = context.Envelope!.Destination!;
        var subscription = destination.Scheme == "local" ? messaging.Named(destination.Host) : ports[destination.Port];
        return subscription.DeliverAsync(context.Runtime.Services.GetRequiredService<IServiceScopeFactory>(), context.Envelope.Message!, ct);
    }
}
