using System.Net;
using System.Security.Cryptography;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Aspire.Tests;

// Stories 60 and 61 of spec #115: the Proving Sample's AppHost, built and started headless with the dashboard off, as the
// testing builder runs it.
public sealed class AppHostTests
{
    // Fixed, so the volumes one run initialises with these credentials serve the next. The start test keeps its own volumes,
    // so a developer's, initialised with the passwords the AppHost generates, are never opened with these.
    private static readonly string[] Arguments =
    [
        "Parameters:postgres-password=apphost-tests",
        "Parameters:cache-password=apphost-tests",
        "Parameters:broker-password=apphost-tests",
        $"Parameters:bearer-signing-key={Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}",
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
        Assert.Equal(["broker", "inventory-db"], References(builder, "inventory"));
        Assert.Equal(["bearer-signing-key", "broker", "cache", "modular-monolith-db"], References(builder, "modular-monolith"));
    }

    [Fact]
    public async Task The_apphost_starts_headless_and_each_host_answers_on_its_resources()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.ModularMonolith_AppHost>(Arguments, timeout.Token);
        foreach (var container in builder.Resources.OfType<ContainerResource>())
        {
            var mount = container.Annotations.OfType<ContainerMountAnnotation>().Single();
            container.Annotations.Remove(mount);
            container.Annotations.Add(new ContainerMountAnnotation($"{mount.Source}-tests", mount.Target, mount.Type, mount.IsReadOnly));
        }

        await using var app = await builder.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);

        await app.ResourceNotifications.WaitForResourceHealthyAsync("inventory", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("modular-monolith", timeout.Token);
        using var inventory = app.CreateHttpClient("inventory");
        using var alive = await inventory.GetAsync("/alive", timeout.Token);
        using var shop = app.CreateHttpClient("modular-monolith");
        // Started, so its bearer options validated, and serving: an anonymous caller is refused, not left unanswered.
        using var anonymous = await shop.GetAsync("/catalog/products", timeout.Token);

        Assert.Equal((HttpStatusCode.OK, "Healthy"), (alive.StatusCode, await alive.Content.ReadAsStringAsync(timeout.Token)));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        // Headless: the running model is still the declared one, which a dashboard would have joined as it started.
        Assert.Equal(Declared, Names(app.Services.GetRequiredService<DistributedApplicationModel>().Resources));
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
