using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Host;
using ModularMonolith.Tests;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using PouryaKit.Web;

[assembly: AssemblyFixture(typeof(AppFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace ModularMonolith.Tests;

/// <summary>
/// The Proving Sample's Host, with the test-only <see cref="StubModule"/> beside Catalog, served in memory,
/// authenticating callers with <see cref="TestAuthentication"/>, whose tenant claim fills the tenant, delivering at test
/// speed, and committing through the <see cref="CommitProbe"/>.
/// </summary>
public sealed class AppFixture : HostFixture
{
    protected override IHost CreateHost(string connectionString) =>
        ComposeHost("Development", connectionString, Logs);

    /// <summary>
    /// The application as the tests host it, in <paramref name="environment"/>, built but not started;
    /// <paramref name="configure"/> may change its composition first. Starting it starts Wolverine on the database's
    /// message store, so a test that starts it stops or disposes it before it ends.
    /// </summary>
    public static WebApplication ComposeHost(
        string environment,
        string connectionString,
        RecordedLogs logs,
        Action<WebApplicationBuilder>? configure = null
    )
    {
        var builder = ModularMonolithHost.CreateBuilder(
            ["--environment", environment, $"--ConnectionStrings:app={connectionString}"]
        );
        builder.AddPouryaKitModules(modules => modules.Add<StubModule>());
        builder.Services.UsePouryaKitTestDelivery();
        CommitProbe.Install(builder.Services);
        DeliveryProbes.Install(builder.Services);
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, null);
        builder.Services.Configure<PouryaKitWebOptions>(web => web.TenantClaim = TestAuthentication.TenantClaim);
        builder.Logging.AddProvider(logs);
        configure?.Invoke(builder);
        return ModularMonolithHost.Build(builder);
    }

    /// <summary>Every log entry the Host has written.</summary>
    public RecordedLogs Logs { get; } = new();

    /// <summary>
    /// A client of the Host's HTTP pipeline, authenticated as the person <paramref name="caller"/>, or anonymous.
    /// </summary>
    public HttpClient Client(string? caller = null, string? tenant = null) => ClientOf(Host, caller, tenant);

    /// <summary>A client of <paramref name="host"/>'s HTTP pipeline, as <see cref="Client"/> makes one.</summary>
    public static HttpClient ClientOf(IHost host, string? caller = null, string? tenant = null)
    {
        var client = host.GetTestClient();
        if (caller is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthentication.SchemeName, caller);
        }

        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthentication.TenantHeader, tenant);
        }

        return client;
    }

    /// <summary>Every row <paramref name="sql"/> returns, read on a connection of its own.</summary>
    public async Task<List<object[]>> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        List<object[]> rows = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }

        return rows;
    }

    public async Task<T> ScalarAsync<T>(string sql) => (T)(await RowsAsync(sql))[0][0];

    /// <summary>Waits until the SQL <paramref name="condition"/> holds; fails with <paramref name="because"/> after 15 s.</summary>
    public async Task WaitUntilAsync(string condition, string because)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!await ScalarAsync<bool>($"select {condition}"))
        {
            Assert.False(timeout.IsCancellationRequested, because);
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
