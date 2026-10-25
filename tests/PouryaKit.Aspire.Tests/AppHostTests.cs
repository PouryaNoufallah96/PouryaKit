using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Catalog.Contracts.Products;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.AppHost;

namespace PouryaKit.Aspire.Tests;

// Stories 60 and 61 of spec #115: the Proving Sample's AppHost, built and started headless with the dashboard off, as the
// testing builder runs it.
public sealed class AppHostTests
{
    // The opt-in of AiTests' Ollama probe: the local agent's start test pulls the same image and model.
    private const string OllamaProbe = "POURYAKIT_OLLAMA_PROBE";

    private static readonly byte[] SigningKey = RandomNumberGenerator.GetBytes(32);

    // Fixed, so the volumes one run initialises with these credentials serve the next. The start test keeps its own volumes,
    // so a developer's, initialised with the passwords the AppHost generates, are never opened with these.
    private static readonly string[] Arguments =
    [
        "Parameters:postgres-password=apphost-tests",
        "Parameters:cache-password=apphost-tests",
        "Parameters:broker-password=apphost-tests",
        $"Parameters:bearer-signing-key={Convert.ToBase64String(SigningKey)}",
    ];

    // The whole model as an allow-list: a dashboard, tracing or logging resource, or one more of anything, is a name here.
    // Aspire adds a hidden rebuilder beside each project.
    private static readonly string[] Declared =
    [
        "bearer-signing-key",
        "broker",
        "cache",
        "inventory",
        "inventory-db",
        "inventory-rebuilder",
        "modular-monolith",
        "modular-monolith-db",
        "modular-monolith-rebuilder",
        "postgres",
        "reverse-proxy",
        "reverse-proxy-rebuilder",
    ];

    [Fact]
    public async Task The_apphost_declares_the_kits_resources_alone_each_on_a_pinned_image_with_a_named_volume()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ModularMonolith_AppHost>(
            Arguments,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(Declared, Names(builder.Resources));
        Assert.Equal(
            [
                ("broker", "docker.io/library/rabbitmq:4.3.6", "/var/lib/rabbitmq"),
                ("cache", "docker.io/library/redis:8.10.1", "/data"),
                ("postgres", "docker.io/library/postgres:18.6", "/var/lib/postgresql"),
            ],
            builder.Resources.OfType<ContainerResource>().OrderBy(container => container.Name, StringComparer.Ordinal).Select(Pinned)
        );
        // Each Host is given the resources its composition uses: Inventory.Host caches nothing.
        Assert.Equal(["bearer-signing-key", "broker", "inventory-db"], References(builder, "inventory"));
        Assert.Equal(["bearer-signing-key", "broker", "cache", "modular-monolith-db"], References(builder, "modular-monolith"));
        Assert.Equal(["inventory", "modular-monolith"], References(builder, "reverse-proxy"));
    }

    // Story 20 of spec #161: the proxy's own settings route every path to ModularMonolith.Host with its probe off, and the
    // AppHost adds Inventory's paths, routed to Inventory.Host by its resource name with the probe on. Discovery resolves
    // each name through the endpoint the proxy's reference to that Host gives it.
    [Fact]
    public async Task The_apphost_routes_inventorys_paths_through_the_proxy_to_the_service_by_discovery_with_its_probe_on()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ModularMonolith_AppHost>(
            Arguments,
            TestContext.Current.CancellationToken
        );
        var proxy = builder.Resources.OfType<ProjectResource>().Single(project => project.Name == "reverse-proxy");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(Path.GetDirectoryName(proxy.GetProjectMetadata().ProjectPath)!, "appsettings.json"))
            .AddInMemoryCollection(
                (await EnvironmentAsync(builder, "reverse-proxy")).Select(variable =>
                    KeyValuePair.Create(variable.Key.Replace("__", ":", StringComparison.Ordinal), (string?)variable.Value)
                )
            )
            .Build();
        (string Key, string? Value)[] expected =
        [
            ("ReverseProxy:Routes:inventory:ClusterId", "inventory"),
            ("ReverseProxy:Routes:inventory:Match:Path", "/inventory/{**catch-all}"),
            ("ReverseProxy:Clusters:inventory:Destinations:host:Address", "http://inventory"),
            ("ReverseProxy:Clusters:inventory:HealthCheck:Active:Enabled", "true"),
            ("Services:inventory:http:0", "{inventory.bindings.http.url}"),
            ("ReverseProxy:Routes:modular-monolith:Match:Path", "{**catch-all}"),
            ("ReverseProxy:Clusters:modular-monolith:Destinations:host:Address", "http://modular-monolith"),
            ("ReverseProxy:Clusters:modular-monolith:HealthCheck:Active:Enabled", "False"),
            ("Services:modular-monolith:http:0", "{modular-monolith.bindings.http.url}"),
        ];

        Assert.Equal(expected, expected.Select(setting => (setting.Key, configuration[setting.Key])));
    }

    [Fact]
    public async Task With_a_local_agent_the_apphost_adds_ollama_alone_on_a_pinned_image_with_a_named_volume()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ModularMonolith_AppHost>(
            [.. Arguments, "LocalAgent=true"],
            TestContext.Current.CancellationToken
        );

        Assert.Equal(Declared.Append("ollama").Order(StringComparer.Ordinal), Names(builder.Resources));
        var ollama = builder.Resources.OfType<ContainerResource>().Single(container => container.Name == "ollama");
        Assert.Equal(("ollama", "docker.io/ollama/ollama:0.40.6", "/root/.ollama"), Pinned(ollama));
        Assert.Equal(["ollama_model"], ollama.Annotations.OfType<HealthCheckAnnotation>().Select(check => check.Key));
        // The Host waits for its model, and Inventory.Host, which runs no agent, is not given it.
        Assert.Contains(
            builder.Resources.Single(resource => resource.Name == "modular-monolith").Annotations.OfType<WaitAnnotation>(),
            wait => wait.Resource == ollama
        );
        Assert.DoesNotContain(
            builder.Resources.Single(resource => resource.Name == "inventory").Annotations.OfType<WaitAnnotation>(),
            wait => wait.Resource == ollama
        );
        // The agent's PouryaKit:AI section points at the container's OpenAI-compatible API and the model it serves.
        Assert.Equal(
            [
                new("PouryaKit__AI__Endpoint", "{ollama.bindings.http.url}/v1"),
                new("PouryaKit__AI__Model", "qwen2.5:0.5b"),
                new("PouryaKit__AI__Provider", "Ollama"),
            ],
            await AgentSettingsAsync(builder, "modular-monolith")
        );
        Assert.Empty(await AgentSettingsAsync(builder, "inventory"));
    }

    private static async Task<IEnumerable<KeyValuePair<string, string>>> AgentSettingsAsync(
        IDistributedApplicationTestingBuilder builder,
        string project
    ) =>
        (await EnvironmentAsync(builder, project))
            .Where(variable => variable.Key.StartsWith("PouryaKit__AI__", StringComparison.Ordinal))
            .OrderBy(variable => variable.Key, StringComparer.Ordinal);

    // The environment the AppHost gives a resource, as the publish manifest reads it: an endpoint is its expression,
    // allocated only when the AppHost runs.
    private static async Task<IEnumerable<KeyValuePair<string, string>>> EnvironmentAsync(IDistributedApplicationTestingBuilder builder, string project)
    {
        var host = builder.Resources.Single(resource => resource.Name == project);
        var context = new EnvironmentCallbackContext(new(DistributedApplicationOperation.Publish), host, []);
        foreach (var callback in host.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        return context.EnvironmentVariables.Select(variable =>
            KeyValuePair.Create(variable.Key, variable.Value is IManifestExpressionProvider value ? value.ValueExpression : $"{variable.Value}")
        );
    }

    [Fact]
    public async Task The_apphost_starts_headless_and_each_host_answers_on_its_resources()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using var builder = await StartingAsync(Arguments, timeout.Token);
        await using var app = await builder.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("inventory", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("modular-monolith", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("reverse-proxy", timeout.Token);
        using var inventory = app.CreateHttpClient("inventory");
        using var alive = await inventory.GetAsync("/alive", timeout.Token);
        using var shop = app.CreateHttpClient("modular-monolith");
        // Started, so its bearer options validated, and serving: an anonymous caller is refused, not left unanswered.
        using var anonymous = await AnsweredAsync(() => shop.GetAsync("/catalog/products", timeout.Token), timeout.Token);
        // Through the proxy, every path reaches ModularMonolith.Host but Inventory's, which reach Inventory.Host. Each Host's
        // API versioning refuses a verified caller that names no version on a path of its own Module, and the other Host
        // answers that path 404: ModularMonolith.Host hosts no Inventory beside a broker, Inventory.Host no Catalog. An
        // anonymous caller proves neither route, since both Hosts' fallback policy refuses it on any path. No query runs: no
        // Host applies its migrations, and the AppHost applies none.
        using var proxy = app.CreateHttpClient("reverse-proxy");
        inventory.DefaultRequestHeaders.Authorization = proxy.DefaultRequestHeaders.Authorization = new("Bearer", Token());
        using var proxiedUnversioned = await proxy.GetAsync($"/inventory/stock-items/{Guid.CreateVersion7()}", timeout.Token);
        using var proxiedCatalog = await proxy.GetAsync("/catalog/products", timeout.Token);
        using var catalogAtInventory = await inventory.GetAsync("/catalog/products", timeout.Token);
        // Story 23 of spec #161: Catalog's gRPC service through Aspire's endpoint proxy, a call with HTTP/2's prior knowledge over
        // cleartext. The proxy, on a port of its own, carries it to the Host's listener of HTTP/2 alone, and the service refuses
        // an id that is no Guid before any query; the http endpoint's listener, which negotiates no HTTP/2 without TLS, refuses
        // the call itself.
        var grpc = app.GetEndpoint("modular-monolith", "grpc");
        var proxiedGrpc = await GetProductFailureAsync(grpc, timeout.Token);
        var proxiedToHttp = await GetProductFailureAsync(app.GetEndpoint("modular-monolith", "http"), timeout.Token);
        var running = await app.ResourceNotifications.WaitForResourceAsync(
            "modular-monolith",
            resource => resource.Snapshot.State?.Text == KnownResourceStates.Running,
            timeout.Token
        );
        var listener = running.Snapshot.EnvironmentVariables.Single(variable => variable.Name == "Kestrel__Endpoints__grpc__Url").Value;

        Assert.Equal((HttpStatusCode.OK, "Healthy"), (alive.StatusCode, await alive.Content.ReadAsStringAsync(timeout.Token)));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, proxiedUnversioned.StatusCode);
        Assert.Contains("\"ApiVersionUnspecified\"", await proxiedUnversioned.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, proxiedCatalog.StatusCode);
        Assert.Contains("\"ApiVersionUnspecified\"", await proxiedCatalog.Content.ReadAsStringAsync(timeout.Token), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, catalogAtInventory.StatusCode);
        Assert.Equal((StatusCode.InvalidArgument, "product_id is not a Guid."), (proxiedGrpc.StatusCode, proxiedGrpc.Status.Detail));
        Assert.NotEqual(new Uri(listener!).Port, grpc.Port);
        Assert.Equal(StatusCode.Internal, proxiedToHttp.StatusCode);
        Assert.Contains("HTTP_1_1_REQUIRED", proxiedToHttp.Status.Detail, StringComparison.Ordinal);
        // Headless: the running model is still the declared one, which a dashboard would have joined as it started.
        Assert.Equal(Declared, Names(app.Services.GetRequiredService<DistributedApplicationModel>().Resources));
    }

    [Fact]
    public async Task With_a_local_agent_the_apphost_starts_ollama_and_the_hosts_agent_answers_through_it()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(OllamaProbe) == "1",
            $"The local agent's start test runs only with {OllamaProbe}=1: it pulls ollama/ollama:0.40.6 and the model qwen2.5:0.5b."
        );
        // A first run pulls a 7 GB image and the model.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        await using var builder = await StartingAsync([.. Arguments, "LocalAgent=true"], timeout.Token);
        await using var app = await builder.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("ollama", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("modular-monolith", timeout.Token);
        using var shop = app.CreateHttpClient("modular-monolith");
        shop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token());
        shop.Timeout = TimeSpan.FromMinutes(5);
        using var answered = await AnsweredAsync(
            () => shop.PostAsJsonAsync("/catalog/v2/products/ask", new { question = "Which tools can you call?" }, timeout.Token),
            timeout.Token
        );
        var answer = await answered.Content.ReadAsStringAsync(timeout.Token);

        TestContext.Current.TestOutputHelper?.WriteLine($"qwen2.5:0.5b answered through ModularMonolith.Host: {answer}");
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(JsonSerializer.Deserialize<string>(answer)));
    }

    // The sample's AppHost to start headless, its volumes moved to names of the tests' own. The testing builder replaces the
    // AppHost's options with its own, dashboard off whatever the AppHost says: take the sample's own choice instead.
    private static async Task<IDistributedApplicationTestingBuilder> StartingAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ModularMonolith_AppHost>(
            arguments,
            (options, _) => options.DisableDashboard = AppHostOptions.Create([]).DisableDashboard,
            cancellationToken
        );
        foreach (var container in builder.Resources.OfType<ContainerResource>())
        {
            var mount = container.Annotations.OfType<ContainerMountAnnotation>().Single();
            container.Annotations.Remove(mount);
            container.Annotations.Add(new ContainerMountAnnotation($"{mount.Source}-tests", mount.Target, mount.Type, mount.IsReadOnly));
        }

        return builder;
    }

    // A caller's bearer token as the AppHost's Hosts verify it: its issuer, audience and the parameter's key.
    private static string Token()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    iss = "modular-monolith-apphost",
                    aud = "modular-monolith",
                    sub = "apphost-tests",
                    tenant = "apphost-tests",
                    nbf = now,
                    exp = now + 600,
                }
            )
        );
        return $"{header}.{payload}.{Base64Url(HMACSHA256.HashData(SigningKey, Encoding.ASCII.GetBytes($"{header}.{payload}")))}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Catalog's ProductService.GetProduct at <address>, for a product id that is no Guid, by a caller with a bearer token
    // signed with the test's key: the failure the call ends in.
    private static async Task<RpcException> GetProductFailureAsync(Uri address, CancellationToken cancellationToken)
    {
        using var channel = GrpcChannel.ForAddress(address);
        return await Assert.ThrowsAsync<RpcException>(async () =>
            await new ProductService.ProductServiceClient(channel).GetProductAsync(
                new GetProductRequest { ProductId = "not-a-guid" },
                new Metadata { { "authorization", $"Bearer {Token()}" } },
                cancellationToken: cancellationToken
            )
        );
    }

    // ModularMonolith.Host has no health check, so its resource is healthy once its process runs, which can be before Kestrel
    // listens: ask again until it answers, within the test's timeout.
    private static async Task<HttpResponseMessage> AnsweredAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return await send();
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
        }
    }

    private static IEnumerable<string> Names(IEnumerable<IResource> resources) =>
        resources.Select(resource => resource.Name).Order(StringComparer.Ordinal);

    // The image as registry/image:tag, and the target of its one mount, which must be a volume with a name.
    private static (string, string, string) Pinned(ContainerResource container)
    {
        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        var mount = Assert.Single(container.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(ContainerMountType.Volume, mount.Type);
        Assert.False(string.IsNullOrWhiteSpace(mount.Source));
        return (container.Name, $"{image.Registry}/{image.Image}:{image.Tag}", mount.Target);
    }

    private static IEnumerable<string> References(IDistributedApplicationTestingBuilder builder, string project) =>
        builder
            .Resources.Single(resource => resource.Name == project)
            .Annotations.OfType<ResourceRelationshipAnnotation>()
            .Where(relationship => relationship.Type == "Reference")
            .Select(relationship => relationship.Resource.Name)
            .Distinct()
            .Order(StringComparer.Ordinal);
}
