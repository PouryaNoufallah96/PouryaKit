using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModularMonolith.Host;
using Npgsql;
using PouryaKit.Testing;
using Testcontainers.PostgreSql;
using W3cPropagation;

[assembly: AssemblyFixture(typeof(PostgresFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace W3cPropagation;

/// <summary>One PostgreSQL container for the run; every test gets a database of its own.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.6").Build();

    public async Task<string> NewDatabaseAsync()
    {
        var name = $"t{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var create = new NpgsqlCommand($"create database {name}", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();
}

/// <summary>
/// The Proving Sample's own composition (Catalog, Inventory, Billing; the Kit's EF Outbox and routed durable local queues),
/// delivering at test speed, served in memory, with the Spike's probes: the stand-in Operation activity around every
/// dispatcher call, and the observation points in the Outbox and the Receipts.
/// </summary>
internal static class SpikeHost
{
    public static WebApplication Compose(string connectionString, string hostName)
    {
        var builder = ModularMonolithHost.CreateBuilder(["--environment", "Development", $"--ConnectionStrings:app={connectionString}"]);
        builder.Services.UsePouryaKitTestDelivery();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        TraceProbe.Install(builder.Services, hostName);
        return ModularMonolithHost.Build(builder);
    }

    public static async Task<WebApplication> StartAsync(string connectionString, string hostName)
    {
        var host = Compose(connectionString, hostName);
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        return host;
    }
}
