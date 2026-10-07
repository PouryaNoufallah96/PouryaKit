using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace PouryaKit.Messaging.Wolverine;

// The one check at Wolverine's own moment: it runs once every hosted service has started, Wolverine included, so it sees
// the queues as Wolverine built them. Wolverine compiles its endpoints only as it starts (Endpoint.Compile applies its
// endpoint policies, such as UseDurableLocalQueues, then a Host's own queue configuration), so a composition check at
// the build, or a startup check before any hosted service starts, would read every queue's default. A Notification, an
// Integration Event, a Snapshot or a delayed command is durable only when its queue stores it (and an Outbox writes it,
// OutboxCheck); otherwise the business change would commit with nothing behind it, so the Host does not start.
internal sealed class DurableQueuesCheck(Subscriptions subscriptions, IServiceProvider services) : IHostedLifecycleService
{
    // Every declaration registers both.
    public static void AddTo(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, OutboxCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DurableQueuesCheck>());
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var delayedCommands = services.GetService<DelayedCommandDeclarations>()?.All ?? [];
        if (!subscriptions.All.Any() && subscriptions.Publications.Count == 0 && !delayedCommands.Any())
        {
            return Task.CompletedTask;
        }

        var transports = services.GetRequiredService<IWolverineRuntime>().Options.Transports;
        List<string> notDurable =
        [
            .. subscriptions
                .All.Select(subscription => subscription.Name)
                .Concat(delayedCommands.Select(delayed => delayed.Name))
                .Select(name => new Uri($"local://{name}"))
                .Select(address => (Address: address, Queue: transports.TryGetEndpoint(address)))
                .Where(queue =>
                    queue.Queue is not { Role: EndpointRole.Application, Mode: EndpointMode.Durable }
                )
                .Select(queue => $"{queue.Address} ({queue.Queue?.Role}, {queue.Queue?.Mode})"),
        ];
        if (notDurable.Count > 0)
        {
            throw new InvalidOperationException(
                $"The queues of these subscriptions or delayed commands do not store what they receive, so their deliveries would not survive a restart: {string.Join(", ", notDurable)}. Each such queue is a durable application queue; rename the subscription or delayed command, or remove the configuration that changed its queue."
            );
        }

        // What arrives from another Host, at the Host's own queue on a broker, is stored before it is acknowledged, and a
        // delivery that fails there is set aside in the message store, as every subscription's is.
        List<string> brokerListeners =
        [
            .. transports
                .AllEndpoints()
                .Where(endpoint =>
                    endpoint is { IsListener: true, Role: EndpointRole.Application } and IBrokerEndpoint
                    && (endpoint.Mode != EndpointMode.Durable || endpoint.DeadLetterStorage != DeadLetterStorageMode.Durable)
                )
                .Select(endpoint => $"{endpoint.Uri} ({endpoint.Mode}, dead letters {endpoint.DeadLetterStorage})"),
        ];
        return brokerListeners.Count == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException(
                $"These broker listeners receive from other Hosts but do not store what they receive in a durable Inbox, or set dead letters aside outside the message store: {string.Join(", ", brokerListeners)}. Each is Durable with dead letters Durable; remove the configuration that changed it, such as BufferedInMemory() or a broker dead-letter queue."
            );
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
