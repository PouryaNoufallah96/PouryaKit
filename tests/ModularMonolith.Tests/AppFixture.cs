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

[assembly: AssemblyFixture(typeof(AppFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace ModularMonolith.Tests;

/// <summary>
/// The Proving Sample's Host, with the test-only <see cref="StubModule"/> beside Catalog, served in memory,
/// authenticating callers with <see cref="TestAuthentication"/>, delivering at test speed, and committing through the
/// <see cref="CommitProbe"/>.
/// </summary>
public sealed class AppFixture : HostFixture
{
    protected override IHost CreateHost(string connectionString) => Compose(connectionString, configure: null);

    /// <summary>
    /// Another Host of the same application on the fixture's database, not yet started, with
    /// <paramref name="configure"/> applied before it is built. A test that starts it stops it.
    /// </summary>
    public IHost CreateSecondHost(Action<WebApplicationBuilder>? configure = null) => Compose(ConnectionString, configure);

    private IHost Compose(string connectionString, Action<WebApplicationBuilder>? configure)
    {
        var builder = ModularMonolithHost.CreateBuilder(
            ["--environment", "Development", $"--ConnectionStrings:app={connectionString}"]
        );
        builder.AddPouryaKitModules(modules => modules.Add<StubModule>());
        builder.Services.UsePouryaKitTestDelivery();
        CommitProbe.Install(builder.Services);
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, null);
        builder.Logging.AddProvider(Logs);
        configure?.Invoke(builder);
        return ModularMonolithHost.Build(builder);
    }

    /// <summary>Every log entry the Host has written.</summary>
    public RecordedLogs Logs { get; } = new();

    /// <summary>
    /// A client of the Host's HTTP pipeline, authenticated as the person <paramref name="caller"/>, or anonymous.
    /// </summary>
    public HttpClient Client(string? caller = null)
    {
        var client = Host.GetTestClient();
        if (caller is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthentication.SchemeName, caller);
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
}
