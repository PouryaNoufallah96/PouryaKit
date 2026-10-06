using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine;

// Runs once every hosted service has started, Wolverine included, so it sees the queues as Wolverine built them. A
// Notification, an Integration Event, a Snapshot or a delayed command is durable only when an Outbox writes it in the Unit of
// Work's commit and its queue stores it; otherwise the business change would commit with nothing behind it, so the Host does
// not start.
internal sealed class StartupCheck(Subscriptions subscriptions, IServiceProvider services) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        var delayedCommands = services.GetService<DelayedCommandDeclarations>()?.All ?? [];
        if (!subscriptions.All.Any() && subscriptions.Publications.Count == 0 && !delayedCommands.Any())
        {
            return Task.CompletedTask;
        }

        if (!services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(INotificationOutbox)))
        {
            throw new InvalidOperationException(
                "Notification Handlers, Integration Events, Snapshots or delayed commands are declared, but nothing writes them to an Outbox, so every Operation would commit and none would be delivered. Call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore on the Host."
            );
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
        return notDurable.Count == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException(
                $"The queues of these subscriptions or delayed commands do not store what they receive, so their deliveries would not survive a restart: {string.Join(", ", notDurable)}. Each such queue is a durable application queue; rename the subscription or delayed command, or remove the configuration that changed its queue."
            );
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
