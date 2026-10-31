using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Categories.ValueObjects;
using Catalog.Products.Events.DomainEvents;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.ValueObjects;
using Inventory.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Testing;
using Wolverine;
using Wolverine.Runtime.Serialization;

namespace ModularMonolith.Tests;

// Deliveries into a tenant-scoped Module run under the tenant they carry, and one that carries none is refused before its
// Receipt and its handler, retried, then set aside naming the reason (ticket #117). Catalog is tenant-scoped; Inventory,
// which runs a recurring command, is not, and keys its copy of Catalog's products by tenant and product.
public sealed class TenantDeliveryTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string Notification = "catalog.product-created.publish-product-created";

    // A Notification of Catalog's own, and another Host's Integration Event and Snapshot that Catalog receives into a
    // handler that writes with plain SQL only, each sent to its subscription's queue as the Outbox or a fan-out would.
    [Theory]
    [InlineData(Notification)]
    [InlineData(TenantProbes.Subscription)]
    [InlineData(TenantProbes.SnapshotSubscription)]
    public async Task A_delivery_with_no_tenant_into_a_tenant_scoped_module_writes_nothing_and_is_dead_lettered_naming_the_reason(
        string subscription
    )
    {
        var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => SendAsync(subscription, tenant: null),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            [new Delivery(subscription, WireNameOf(subscription), DeadLettered: true)],
            deliveries
        );
        var deadLetter = Assert.Single(
            await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken)
        );
        Assert.Contains(
            $"{TenantErrors.Required.Code} ({TenantErrors.Required.Message})",
            deadLetter.ExceptionMessage
        );
        // Retried, and every attempt refused before its Receipt and its handler.
        Assert.Equal(
            1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count,
            EnvelopeSerializer
                .Deserialize(
                    await Fixture.ScalarAsync<byte[]>(
                        "select body from wolverine.wolverine_dead_letters"
                    )
                )
                .Attempts
        );
        Assert.Empty(await Fixture.RowsAsync("select * from catalog.pouryakit_receipts"));
        Assert.Empty(await Fixture.RowsAsync("select * from stub.note_tallies"));
    }

    [Fact]
    public async Task A_delivery_into_a_tenant_scoped_module_runs_under_the_tenant_it_carries()
    {
        await Fixture.WaitForDeliveriesAsync(
            () => SendAsync(TenantProbes.Subscription, "north"),
            TestContext.Current.CancellationToken
        );
        await Fixture.WaitForDeliveriesAsync(
            () => SendAsync(TenantProbes.Subscription, "south"),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [
                ["probe north"],
                ["probe south"],
            ],
            await Fixture.RowsAsync("select text from stub.note_tallies order by text")
        );
    }

    // Catalog publishes each product's Snapshot and its ProductCreatedV2 under the tenant that created it; Inventory's
    // copies are keyed by tenant and product and written under that tenant, so none is another tenant's to read.
    [Fact]
    public async Task A_snapshot_and_an_integration_event_delivered_under_one_tenant_write_inventorys_copies_for_that_tenant_only()
    {
        var north = await CreateProductAsync("north");
        var south = await CreateProductAsync("south");

        Assert.Empty(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            [
                ["north", north, "Lamp"],
                ["south", south, "Lamp"],
            ],
            await Fixture.RowsAsync(
                "select tenant, product_id, name from inventory.catalog_products order by tenant"
            )
        );
        Assert.Equal(
            [
                ["north", north],
                ["south", south],
            ],
            await Fixture.RowsAsync(
                "select tenant, product_id from inventory.product_categories order by tenant"
            )
        );
    }

    // A copy made before Inventory kept the tenant can never learn it (review F2), so the migration that keys the copies by
    // tenant refuses a table that holds one, and applies to an empty one. It is undone and applied again here.
    [Fact]
    public async Task The_migration_that_keys_inventorys_copies_by_tenant_refuses_a_table_holding_a_copy_with_no_tenant()
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        var migrator = scope
            .ServiceProvider.GetRequiredService<InventoryDbContext>()
            .GetService<IMigrator>();
        await migrator.MigrateAsync(
            "20261016120000_StockCounts",
            TestContext.Current.CancellationToken
        );
        try
        {
            await Fixture.RowsAsync(
                $"insert into inventory.catalog_products (product_id, name, version) values ('{Guid.CreateVersion7()}', 'Lamp', 1)"
            );

            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken)
            );

            Assert.Equal(
                "Inventory holds copies of Catalog's products that kept no tenant, and their tenant cannot be recovered, so this migration refuses to guess one.",
                refused.MessageText
            );
        }
        finally
        {
            await Fixture.RowsAsync("delete from inventory.catalog_products");
            await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(
            [
                ["tenant", "product_id"],
            ],
            await Fixture.RowsAsync(
                "select a.attname, b.attname from pg_index i join pg_attribute a on a.attrelid = i.indrelid and a.attnum = i.indkey[0] join pg_attribute b on b.attrelid = i.indrelid and b.attnum = i.indkey[1] where i.indrelid = 'inventory.catalog_products'::regclass and i.indisprimary"
            )
        );
    }

    private static string WireNameOf(string subscription) =>
        subscription switch
        {
            Notification => "catalog.product-created",
            TenantProbes.Subscription => "tests.tenant-probe.v1",
            _ => "tests.tenant-probe-snapshot.v1",
        };

    // Straight through Wolverine to the subscription's queue, with the headers the Kit's Outbox writes.
    private async Task<bool> SendAsync(string subscription, string? tenant)
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        var options = new DeliveryOptions().WithOperationContext(
            new OperationContext(GatewayTestExtensions.TestActor, tenant)
        );
        object message = subscription switch
        {
            Notification => new ProductCreatedDomainEvent(
                new ProductId(Guid.CreateVersion7()),
                new CategoryId(Guid.CreateVersion7()),
                "Lamp"
            ),
            TenantProbes.Subscription => new TenantProbeV1("probe"),
            _ => new TenantProbeSnapshotV1("probe"),
        };
        options.Headers[IntegrationEventHeaders.DomainEventId] = Guid.CreateVersion7().ToString();
        options.Headers[SnapshotHeaders.AggregateId] = "probe";
        options.Headers[SnapshotHeaders.Version] = "1";
        await scope
            .ServiceProvider.GetRequiredService<IMessageBus>()
            .EndpointFor(new Uri($"local://{subscription}"))
            .SendAsync(message, options);
        return true;
    }

    private async Task<Guid> CreateProductAsync(string tenant)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var context = new OperationContext(GatewayTestExtensions.TestActor, tenant);
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.True(
            (
                await catalog.SendAsync(
                    new CreateCategoryCommand(category, "Lamps"),
                    context,
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );
        await Fixture.WaitForDeliveriesAsync(
            () =>
                catalog.SendAsync(
                    new CreateProductCommand(product, category, "Lamp"),
                    context,
                    TestContext.Current.CancellationToken
                ),
            TestContext.Current.CancellationToken
        );
        return product;
    }
}
