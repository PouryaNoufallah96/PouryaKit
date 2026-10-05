using JasperFx;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Postgresql;

namespace S1;

public sealed record SpikeSettings(
    string ConnectionString,
    TransactionMiddlewareMode EfMode = TransactionMiddlewareMode.Eager,
    EnvelopeMapping Mapping = EnvelopeMapping.Raw,
    DurabilityMode Durability = DurabilityMode.Balanced,
    bool ShortDurabilityTimers = true,
    TimeSpan? InboxStaleTime = null);

/// <summary>
/// One Host container holding two Modules (Catalog and Inventory), each with its own DbContext and schema
/// in one PostgreSQL database, and one Wolverine message store with durable local queues.
/// </summary>
public static class SpikeHost
{
    /// <summary>Wolverine's default envelope schema for PostgreSQL, stated here so the Spike can assert it.</summary>
    public const string MessageStoreSchema = "wolverine";

    public static IHost Build(SpikeSettings settings, Action<ILoggingBuilder>? logging = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        logging?.Invoke(builder.Logging);

        var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        builder.Services.AddSingleton(dataSource);
        builder.Services.AddSingleton(new SpikeModelOptions(settings.Mapping));

        // No EnableRetryOnFailure: the Kit runs no retrying execution strategy around handlers.
        builder.Services.AddDbContext<CatalogDbContext>(o => o
            .UseNpgsql(dataSource)
            .ReplaceService<IModelCacheKeyFactory, MappingAwareModelCacheKeyFactory>());
        builder.Services.AddDbContext<InventoryDbContext>(o => o
            .UseNpgsql(dataSource)
            .ReplaceService<IModelCacheKeyFactory, MappingAwareModelCacheKeyFactory>());

        builder.Services.AddScoped(typeof(SpikeUnitOfWork<>));

        builder.UseWolverine(opts =>
        {
            opts.ServiceName = "s1-host";
            opts.Discovery.IncludeAssembly(typeof(SpikeHost).Assembly);

            // One message store for the whole Host, shared by both Modules' DbContexts.
            opts.PersistMessagesWithPostgresql(settings.ConnectionString);

            // Registers IDbContextOutbox<T>; the mode only governs Wolverine's generated handler middleware.
            opts.UseEntityFrameworkCoreTransactions(settings.EfMode);

            opts.Policies.UseDurableLocalQueues();

            opts.Durability.Mode = settings.Durability;
            // Null by default. When set, the durability agent releases any inbox row older than this,
            // even one owned by a live node, and Wolverine adds a "timestamp" column to the inbox table.
            opts.Durability.InboxStaleTime = settings.InboxStaleTime;
            if (!settings.ShortDurabilityTimers) return;
            // Short timers so a killed node is detected and its envelopes recovered within a test's budget.
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

    /// <summary>The Modules' business tables and the Spike's delivery log. Wolverine builds its own tables at startup.</summary>
    public const string BusinessSchemaSql = """
        create schema if not exists catalog;
        create schema if not exists inventory;
        create schema if not exists spike;
        create table if not exists catalog.products (id uuid primary key, name text not null);
        create table if not exists inventory.stock_items (id uuid primary key, quantity int not null);
        create table if not exists spike.deliveries (
            id bigserial primary key,
            handler text not null,
            entity_id uuid not null,
            message_id uuid not null,
            delivered_at timestamptz not null default now());
        """;

    public static async Task CreateBusinessSchemaAsync(string connectionString)
    {
        await using var ds = NpgsqlDataSource.Create(connectionString);
        await using var cmd = ds.CreateCommand(BusinessSchemaSql);
        await cmd.ExecuteNonQueryAsync();
    }
}
