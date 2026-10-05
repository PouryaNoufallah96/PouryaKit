using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using S2.Billing;
using S2.Catalog;
using S2.Inventory;
using S2.Kit;
using S2.Kit.Harness;
using S2.Kit.OptionA;
using S2.Kit.OptionB;
using S2.Shipping;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Wolverine.Persistence;
using Wolverine.Postgresql;

namespace S2.Host;

public enum DeliveryChoice
{
    /// <summary>Option A: <c>MultipleHandlerBehavior.Separated</c>, one sticky durable local queue per subscription.</summary>
    SeparatedQueues,

    /// <summary>Option A without pinning a lone subscriber to its queue: Wolverine's default routing for one handler.</summary>
    SeparatedQueuesUnpinned,

    /// <summary>Option B: a Kit-owned delivery envelope per subscription, published to one durable local queue per message type.</summary>
    DeliveryEnvelope,

    /// <summary>Option B variant, rejected: each delivery sent to its subscription's own queue with <c>EndpointFor</c>.</summary>
    DeliveryEnvelopeViaEndpointFor,

    /// <summary>Control: Option A's adapters with Wolverine's default <c>ClassicCombineIntoOneLogicalHandler</c>.</summary>
    ClassicCombined,
}

public sealed record SpikeSettings(
    string ConnectionString,
    DeliveryChoice Delivery,
    bool InventorySubscribes = true,
    bool BillingSubscribes = true,
    bool ShippingSubscribes = false,
    MessageIdentity Identity = MessageIdentity.IdOnly,
    TimeSpan[]? RetryCooldowns = null)
{
    /// <summary>Default: two quick retries, so three attempts, then the error queue.</summary>
    public TimeSpan[] Cooldowns => RetryCooldowns ?? [TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200)];
}

/// <summary>One Host container: Catalog publishes, Inventory and Billing subscribe, one Wolverine message store.</summary>
public static class SpikeHost
{
    public static IHost Build(SpikeSettings settings, Action<ILoggingBuilder>? logging = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        logging?.Invoke(builder.Logging);

        var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton<DeliveryLog>();
        builder.Services.AddDbContext<CatalogDbContext>(o => o.UseNpgsql(dataSource));
        builder.Services.AddScoped(typeof(S2.Kit.UnitOfWork<>));

        // What each Module's Composition Root declares; the Kit turns it into Wolverine configuration.
        var messaging = new KitMessaging();
        // Billing first, so that in the combined control Billing runs before Inventory on every attempt.
        CatalogModule.Configure(messaging);
        if (settings.BillingSubscribes) BillingModule.Configure(messaging);
        if (settings.InventorySubscribes) InventoryModule.Configure(messaging);
        if (settings.ShippingSubscribes) ShippingModule.Configure(messaging);

        IDeliveryOption delivery = settings.Delivery switch
        {
            DeliveryChoice.SeparatedQueues => new SeparatedQueues(),
            DeliveryChoice.SeparatedQueuesUnpinned => new SeparatedQueues(pinSingleSubscriber: false),
            DeliveryChoice.ClassicCombined => new SeparatedQueues(separated: false),
            DeliveryChoice.DeliveryEnvelopeViaEndpointFor => new DeliveryEnvelope(queuePerSubscription: true),
            _ => new DeliveryEnvelope(),
        };
        builder.Services.AddSingleton(delivery);

        builder.UseWolverine(opts =>
        {
            opts.ServiceName = "s2-host";
            opts.PersistMessagesWithPostgresql(settings.ConnectionString);
            opts.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
            opts.Policies.UseDurableLocalQueues();
            opts.Durability.MessageIdentity = settings.Identity;

            WireNames.UseInWolverine();
            delivery.Configure(messaging, builder.Services, opts);

            opts.OnException<Exception>().RetryWithCooldown(settings.Cooldowns).Then.MoveToErrorQueue();

            // Short timers so a killed node's envelopes are recovered within a test's budget (ADR 0004).
            opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(1);
            opts.Durability.FirstHealthCheckExecution = TimeSpan.FromMilliseconds(200);
            opts.Durability.StaleNodeTimeout = TimeSpan.FromSeconds(3);
            opts.Durability.NodeReassignmentPollingTime = TimeSpan.FromSeconds(1);
            opts.Durability.ScheduledJobPollingTime = TimeSpan.FromSeconds(1);
            opts.Durability.ScheduledJobFirstExecution = TimeSpan.FromMilliseconds(200);
            opts.Durability.OrphanedMessageSweepPollingTime = TimeSpan.FromSeconds(1);
        });

        return builder.Build();
    }

    public const string BusinessSchemaSql = """
        create schema if not exists catalog;
        create schema if not exists spike;
        create table if not exists catalog.products (id uuid primary key, name text not null);
        create table if not exists spike.deliveries (
            id bigserial primary key,
            handler text not null,
            product_id uuid not null,
            event_id uuid not null,
            outcome text not null,
            at timestamptz not null default clock_timestamp());
        """;

    public static async Task CreateBusinessSchemaAsync(string connectionString)
    {
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await using var cmd = ds.CreateCommand(BusinessSchemaSql);
        await cmd.ExecuteNonQueryAsync();
    }
}
