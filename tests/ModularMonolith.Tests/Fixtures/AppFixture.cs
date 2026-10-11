using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Host;
using ModularMonolith.Tests.Stub;
using PouryaKit.Modularity;
using PouryaKit.Security;
using PouryaKit.Testing;
using PouryaKit.Web.OpenApi;

[assembly: AssemblyFixture(typeof(AppFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// The Proving Sample's Host, with the test-only <see cref="StubModule"/> beside Catalog, served in memory,
/// authenticating callers with <see cref="TestAuthentication"/> (its bearer scheme trusts <see cref="TestIssuer"/>),
/// trusting the caller <see cref="TrustedPeer"/> as a peer Host, granting the tests' own Actor the permissions
/// <see cref="TestActorPermissions"/> lists, delivering at test speed, and committing through the
/// <see cref="CommitProbe"/>.
/// </summary>
public sealed class AppFixture : HostFixture
{
    /// <summary>The authenticated caller the Host trusts as a peer, configured as the sample Host reads its peers.</summary>
    public const string TrustedPeer = "inventory-host";

    protected override IHost CreateHost(string connectionString) =>
        ComposeHost("Development", connectionString, Logs);

    /// <summary>
    /// The application as the tests host it, in <paramref name="environment"/>, built but not started;
    /// <paramref name="configure"/> may change its composition first, and <paramref name="arguments"/> add to the
    /// configuration it is composed from. Starting it starts Wolverine on the database's message store, so a test that
    /// starts it stops or disposes it before it ends.
    /// </summary>
    public static WebApplication ComposeHost(
        string environment,
        string connectionString,
        RecordedLogs logs,
        Action<WebApplicationBuilder>? configure = null,
        params string[] arguments
    )
    {
        var builder = ModularMonolithHost.CreateBuilder(
            [
                "--environment",
                environment,
                $"--ConnectionStrings:app={connectionString}",
                $"--PouryaKit:TrustedPeers:Peers:0={TrustedPeer}",
                .. TestIssuer.HostArguments,
                .. arguments,
            ]
        );
        builder.AddPouryaKitModules(modules => modules.Add<StubModule>());
        builder.Services.UsePouryaKitTestDelivery();
        CommitProbe.Install(builder.Services);
        DeliveryProbes.Install(builder.Services);
        ReadProbe.Install(builder.Services);
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, null);
        builder.Services.AddScoped<CallerPermissions>();
        builder.Services.AddScoped<IActorPermissions, TestActorPermissions>();
        builder.Logging.AddProvider(logs);
        configure?.Invoke(builder);
        return ModularMonolithHost.Build(builder);
    }

    /// <summary>The API version the tests' clients name in the <c>api-version</c> header: Catalog's current one.</summary>
    public const string ApiVersion = "2.0";

    /// <summary>Every log entry the Host has written.</summary>
    public RecordedLogs Logs { get; } = new();

    /// <summary>
    /// A client of the Host's HTTP pipeline, authenticated as the person <paramref name="caller"/> holding
    /// <paramref name="permissions"/>, or anonymous, that calls API version <see cref="ApiVersion"/> on a versioned
    /// Module's unversioned paths, such as <c>/catalog/products</c>.
    /// </summary>
    public HttpClient Client(string? caller = null, string? tenant = null, params string[] permissions) =>
        ClientOf(Host, caller, tenant, permissions);

    /// <summary>A client of <paramref name="host"/>'s HTTP pipeline, as <see cref="Client"/> makes one.</summary>
    public static HttpClient ClientOf(IHost host, string? caller = null, string? tenant = null, params string[] permissions)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(WebOpenApiServiceCollectionExtensions.ApiVersionHeader, ApiVersion);
        if (caller is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthentication.SchemeName, caller);
        }

        if (tenant is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthentication.TenantHeader, tenant);
        }

        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add(TestAuthentication.PermissionsHeader, string.Join(',', permissions));
        }

        return client;
    }

    /// <summary>Every row <paramref name="sql"/> returns, read on a connection of its own.</summary>
    public Task<List<object[]>> RowsAsync(string sql) => Sql.RowsAsync(ConnectionString, sql);

    public async Task<T> ScalarAsync<T>(string sql) => (T)(await RowsAsync(sql))[0][0];

    /// <summary>Waits until the SQL <paramref name="condition"/> holds; fails with <paramref name="because"/> after 15 s.</summary>
    public Task WaitUntilAsync(string condition, string because) => Sql.WaitUntilAsync(ConnectionString, condition, because);
}
