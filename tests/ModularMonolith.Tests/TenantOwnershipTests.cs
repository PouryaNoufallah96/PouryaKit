using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// A tenant-scoped aggregate is saved only by its own tenant's Operation (ticket #117): one loaded across tenants, with the
// tenant filter set aside, fails the save with a forbidden Error whatever the Operation changed of it, as does one whose
// tenant the Operation changed, and nothing is saved. The binders are the Stub's; the products are Catalog's, saved in a
// tenant-scoped Module.
public sealed class TenantOwnershipTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private readonly Guid _north = Guid.CreateVersion7();
    private readonly Guid _otherNorth = Guid.CreateVersion7();
    private readonly Guid _south = Guid.CreateVersion7();

    // Each change is made first to the Operation's own tenant's binders, loaded the same way, and saved; then to another
    // tenant's: its root, a sheet, a sheet moved out of it or into it, and its removal, which deletes its sheets with it.
    [Theory]
    [InlineData(BinderChange.Rename, false)]
    [InlineData(BinderChange.RewriteSheet, false)]
    [InlineData(BinderChange.MoveSheet, false)]
    [InlineData(BinderChange.MoveSheet, true)]
    [InlineData(BinderChange.Remove, false)]
    public async Task A_change_to_another_tenants_binder_loaded_with_the_tenant_filter_set_aside_fails_forbidden_and_saves_nothing(
        BinderChange change,
        bool intoTheOther
    )
    {
        await CreateBindersAsync();
        var southBefore = await SouthRowsAsync();

        var own = await ChangeAsync(_north, _otherNorth, change);
        var foreign = intoTheOther
            ? await ChangeAsync(_otherNorth, _south, change)
            : await ChangeAsync(_south, _otherNorth, change);

        Assert.True(own.IsSuccess);
        Assert.Equal(TenantErrors.Mismatch, Assert.Single(foreign.Errors));
        Assert.Equal(southBefore, await SouthRowsAsync());
    }

    [Fact]
    public async Task An_aggregate_added_for_another_tenant_than_the_operations_fails_forbidden_and_saves_nothing()
    {
        var stub = Fixture.Gateway<StubModule>();

        var own = await stub.SendAsync(new CreateBinderCommand(_north, "North", [], "north"), For("north"), TestContext.Current.CancellationToken);
        var foreign = await stub.SendAsync(new CreateBinderCommand(_south, "South", [], "south"), For("north"), TestContext.Current.CancellationToken);

        Assert.True(own.IsSuccess);
        Assert.Equal(TenantErrors.Mismatch, Assert.Single(foreign.Errors));
        Assert.Equal([["north"]], await Fixture.RowsAsync("select tenant from stub.binders"));
    }

    // A product given another tenant than the one it was loaded under: one of south's that a north Operation takes for
    // north, and one of north's that it gives to south. Removed, it is kept marked deleted, and still judged by the tenant
    // it was loaded under.
    [Theory]
    [InlineData("south", false)]
    [InlineData("south", true)]
    [InlineData("north", false)]
    [InlineData("north", true)]
    public async Task A_product_whose_tenant_an_operation_changed_fails_forbidden_whether_then_renamed_or_removed(string owner, bool remove)
    {
        var product = await CreateProductAsync(owner);

        Result reassigned;
        await using (var scope = Fixture.Host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.BeginOperation(For("north"), typeof(CatalogModule));
            reassigned = await scope
                .ServiceProvider.GetRequiredService<IDispatcher>()
                .SendAsync(new ReassignProductCommand(product, owner == "north" ? "south" : "north", remove), TestContext.Current.CancellationToken);
        }

        Assert.Equal(TenantErrors.Mismatch, Assert.Single(reassigned.Errors));
        Assert.Equal(
            [[owner, "Lamp", false, 1L]],
            await Fixture.RowsAsync($"select tenant, name, is_deleted, version from catalog.products where id = '{product}'")
        );
    }

    private static OperationContext For(string tenant) => new(GatewayTestExtensions.TestActor, tenant);

    private async Task CreateBindersAsync()
    {
        var stub = Fixture.Gateway<StubModule>();
        foreach (var (binder, tenant) in new[] { (_north, "north"), (_otherNorth, "north"), (_south, "south") })
        {
            Assert.True(
                (await stub.SendAsync(new CreateBinderCommand(binder, tenant, [$"{tenant} {binder}"]), For(tenant), TestContext.Current.CancellationToken)).IsSuccess
            );
        }
    }

    private Task<Result> ChangeAsync(Guid binder, Guid other, BinderChange change) =>
        Fixture.Gateway<StubModule>().SendAsync(new ChangeBinderCommand(binder, other, change), For("north"), TestContext.Current.CancellationToken);

    // South's binder and every sheet held by a south binder, or once held by one.
    private Task<List<object[]>> SouthRowsAsync() =>
        Fixture.RowsAsync(
            $"""
            select b.id, b.name, b.version, s.id, s.text, s.binder_id from stub.binders b full join stub.sheets s on s.binder_id = b.id
            where b.tenant = 'south' or s.text like 'south %' order by s.id
            """
        );

    private async Task<Guid> CreateProductAsync(string tenant)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.True((await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), For(tenant), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await catalog.SendAsync(new CreateProductCommand(product, category, "Lamp"), For(tenant), TestContext.Current.CancellationToken)).IsSuccess);
        return product;
    }
}
