using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Rmq.Catalog;
using Rmq.Inventory;
using Rmq.Kit;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace Rmq.Tests;

public sealed record ConsumerSettings(
    Topology Topology,
    MessageIdentity? MessageIdentity = null,
    int AuditFailures = 0,
    TimeSpan[]? RetryDelays = null,
    string ServiceName = "inventory")
{
    /// <summary>Topology B's one broker queue of the Host, named after its service.</summary>
    public string HostQueue => ServiceName;

    /// <summary>
    /// Each topology's fair configuration unless a test sets another: A needs <c>IdAndDestination</c>, because the
    /// broker delivers one envelope id to every subscription's queue (<see cref="FanOutIdentityTests"/>); B keeps the
    /// default <c>IdOnly</c>.
    /// </summary>
    public MessageIdentity Identity =>
        MessageIdentity
        ?? (Topology == Topology.QueuePerSubscription ? Wolverine.MessageIdentity.IdAndDestination : Wolverine.MessageIdentity.IdOnly);

    /// <summary>Default: three stored retries a second apart, so four attempts, then the dead-letter storage.</summary>
    public TimeSpan[] Delays => RetryDelays ?? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)];
}

/// <summary>Two Hosts, each with its own database and message store, joined only by the broker.</summary>
public static class Hosts
{
    /// <summary>Catalog's Host: publishes ProductCreatedV1 and has no subscriber of it.</summary>
    public static async Task<IHost> StartProducerAsync(TestEnvironment environment, bool routeToBroker = true)
    {
        var builder = CreateBuilder();
        var messaging = new KitMessaging();
        CatalogModule.Configure(messaging);
        builder.Services.AddDbContext<CatalogDbContext>(options => options.UseNpgsql(environment.ProducerDatabase));
        builder.Services.AddScoped(typeof(UnitOfWork<>));
        builder.UseWolverine(wolverine =>
        {
            Common(wolverine, "catalog", environment.ProducerDatabase, MessageIdentity.IdOnly);
            wolverine.UseEntityFrameworkCoreTransactions();
            BrokerTopology.ConfigureProducer(wolverine, messaging, environment.Broker, routeToBroker);
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>Inventory's Host: two subscriptions to ProductCreatedV1, delivered under <paramref name="settings"/>' topology.</summary>
    public static async Task<IHost> StartConsumerAsync(TestEnvironment environment, ConsumerSettings settings, SpikeHooks? hooks = null)
    {
        var builder = CreateBuilder();
        var messaging = new KitMessaging();
        InventoryModule.Configure(messaging, builder.Services, settings.AuditFailures);
        messaging.AddHandlers(builder.Services);
        // Built by the container, so disposed with the Host.
        builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(environment.ConsumerDatabase));
        builder.Services.AddSingleton<AttemptLog>();
        builder.Services.AddSingleton(hooks ?? new SpikeHooks());
        builder.Services.AddScoped<ModuleTransaction>();
        builder.Services.AddScoped<DeliveryAttempt>();
        builder.UseWolverine(wolverine =>
        {
            Common(wolverine, settings.ServiceName, environment.ConsumerDatabase, settings.Identity);
            wolverine.Policies.UseDurableLocalQueues();
            wolverine.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
            BrokerTopology.ConfigureConsumer(
                wolverine,
                messaging,
                environment.Broker,
                settings.Topology,
                settings.HostQueue,
                settings.Delays
            );
        });
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    /// <summary>Publishes one ProductCreatedV1 through Catalog's Unit of Work; returns its Domain Event id.</summary>
    public static async Task<Guid> PublishAsync(IHost producer, Guid productId, Func<Task>? afterCommitBeforeFlush = null)
    {
        await using var scope = producer.Services.CreateAsyncScope();
        return await CatalogModule.CreateProductAsync(
            scope.ServiceProvider.GetRequiredService<UnitOfWork<CatalogDbContext>>(),
            productId,
            afterCommitBeforeFlush
        );
    }

    /// <summary>Stops a Host as a kill leaves it: no drain, no release of the envelopes it owns.</summary>
    public static async Task KillAsync(IHost host)
    {
        ((WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>()).StopMode = StopMode.Quick;
        await host.StopAsync();
        host.Dispose();
    }

    public static async Task StopAsync(params IHost?[] hosts)
    {
        foreach (var host in hosts)
        {
            if (host is null)
            {
                continue;
            }

            try
            {
                await host.StopAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already killed.
            }

            host.Dispose();
        }
    }

    private static HostApplicationBuilder CreateBuilder()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new TestOutputLoggerProvider()).SetMinimumLevel(LogLevel.Warning);
        return builder;
    }

    private static void Common(WolverineOptions wolverine, string serviceName, string database, MessageIdentity identity)
    {
        wolverine.ServiceName = serviceName;
        wolverine.Discovery.DisableConventionalDiscovery();
        WireNames.UseInWolverine();
        wolverine.PersistMessagesWithPostgresql(database);
        wolverine.Durability.MessageIdentity = identity;
        // Solo: one node per store; a start releases what a killed node owned. Short timers keep a test's budget.
        wolverine.Durability.Mode = DurabilityMode.Solo;
        wolverine.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(1);
        wolverine.Durability.FirstHealthCheckExecution = TimeSpan.FromMilliseconds(200);
        wolverine.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
        wolverine.Durability.ScheduledJobFirstExecution = TimeSpan.FromMilliseconds(200);
        wolverine.Durability.OrphanedMessageSweepPollingTime = TimeSpan.FromSeconds(1);
    }
}
