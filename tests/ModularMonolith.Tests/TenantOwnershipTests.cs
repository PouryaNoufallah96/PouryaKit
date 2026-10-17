using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// A tenant-scoped aggregate is saved only by its own tenant's Operation (ticket #117): one loaded across tenants, with the
// tenant filter set aside, fails the save with a forbidden Error whatever the Operation changed of it, as does one whose
// tenant the Operation changed, and nothing is saved. The binders are the Stub's; the products are Catalog's, saved in a
// tenant-scoped Module. A tenant-scoped aggregate, or a part of one, attached rather than loaded is refused before the
// save, and a loaded part's write matches its row only under the holder it was loaded with (ticket #152). A part queried
// through its own set is read only where its holder is (ticket #153). A Dapper read compares the tenant its lease bound
// (ticket #155).
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

    // A category built with the key and version of a stored one and the Operation's tenant, attached rather than loaded:
    // refused before the save whether the key is the Operation's own tenant's or south's, so neither row changes. With
    // the refusal off, the tenant token still stops south's write, with the conflict Error, and north's own is saved.
    [Theory]
    [InlineData(ForgedWrite.Attach)]
    [InlineData(ForgedWrite.Update)]
    [InlineData(ForgedWrite.Remove)]
    public async Task A_category_attached_rather_than_loaded_fails_forbidden_and_saves_nothing(ForgedWrite write)
    {
        var own = await CreateCategoryAsync("north");
        var south = await CreateCategoryAsync("south");

        var forgedOwn = await ForgeAsync(own, write);
        var forgedSouth = await ForgeAsync(south, write);

        Assert.Equal(
            [["north", "Lamps", 0, 1L], ["south", "Lamps", 0, 1L]],
            await Fixture.RowsAsync(
                $"select tenant, name, product_count, version from catalog.categories where id in ('{own}', '{south}') order by tenant"
            )
        );
        Assert.Equal(TenantErrors.NotLoaded, Assert.Single(forgedOwn.Errors));
        Assert.Equal(TenantErrors.NotLoaded, Assert.Single(forgedSouth.Errors));
    }

    // A sheet built with south's sheet key and attached under north's own binder, loaded through the tenant filter: the
    // binder passes every tenant check and a sheet has no tenant of its own, so only the refusal of an attached part
    // keeps its UPDATE or DELETE off south's row.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_part_attached_under_the_operations_own_aggregate_fails_forbidden_and_saves_nothing(bool remove)
    {
        await CreateBindersAsync();
        var southBefore = await SouthRowsAsync();
        var southSheet = (Guid)Assert.Single(await Fixture.RowsAsync($"select id from stub.sheets where binder_id = '{_south}'"))[0];

        var forged = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new ForgeSheetCommand(_north, southSheet, remove), For("north"), TestContext.Current.CancellationToken);

        Assert.Equal(southBefore, await SouthRowsAsync());
        Assert.Equal(TenantErrors.NotLoaded, Assert.Single(forged.Errors));
    }

    // South's sheet itself, which a query loaded and the Operation then detached or cleared and attached again under
    // north's own binder: attached that time, so refused as a sheet built with its key is, through the entry that tracked
    // it from the query as through a new one.
    [Theory]
    [InlineData(SheetRelease.Detach, false)]
    [InlineData(SheetRelease.Detach, true)]
    [InlineData(SheetRelease.Clear, false)]
    [InlineData(SheetRelease.Clear, true)]
    public async Task A_loaded_part_released_and_attached_again_under_the_operations_own_aggregate_fails_forbidden_and_saves_nothing(
        SheetRelease release,
        bool remove
    )
    {
        await CreateBindersAsync();
        var southBefore = await SouthRowsAsync();
        var southSheet = (Guid)Assert.Single(await Fixture.RowsAsync($"select id from stub.sheets where binder_id = '{_south}'"))[0];

        var reattached = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new ReattachSheetCommand(_north, southSheet, release, remove), For("north"), TestContext.Current.CancellationToken);

        Assert.Equal(southBefore, await SouthRowsAsync());
        Assert.Equal(TenantErrors.NotLoaded, Assert.Single(reattached.Errors));
    }

    // South's sheet, which a query loaded, given north's own binder as its holder both now and as loaded: the binder passes
    // every tenant check and the sheet was loaded, so only the holder's key, a concurrency token, keeps its UPDATE or
    // DELETE off south's row, which still names south's binder.
    [Theory]
    [InlineData(HolderForgery.OriginalValue, false)]
    [InlineData(HolderForgery.OriginalValue, true)]
    [InlineData(HolderForgery.AcceptAllChanges, false)]
    [InlineData(HolderForgery.AcceptAllChanges, true)]
    public async Task A_loaded_part_given_the_operations_own_aggregate_as_its_loaded_holder_fails_with_a_conflict_and_saves_nothing(
        HolderForgery forgery,
        bool remove
    )
    {
        await CreateBindersAsync();
        var southBefore = await SouthRowsAsync();
        var southSheet = (Guid)Assert.Single(await Fixture.RowsAsync($"select id from stub.sheets where binder_id = '{_south}'"))[0];

        var forged = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new ForgeSheetHolderCommand(_north, southSheet, forgery, remove), For("north"), TestContext.Current.CancellationToken);

        Assert.Equal(southBefore, await SouthRowsAsync());
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(forged.Errors));
    }

    // The tenant filter of a sheet is its binder's: a north Operation reads none of south's sheets by id or in the list,
    // south's reads its own, and a read that sets the filter aside crosses tenants.
    [Fact]
    public async Task A_part_queried_through_its_own_set_is_read_only_by_its_aggregates_tenant_unless_the_filter_is_set_aside()
    {
        await CreateBindersAsync();
        var southSheet = (Guid)Assert.Single(await Fixture.RowsAsync($"select id from stub.sheets where binder_id = '{_south}'"))[0];
        var stub = Fixture.Gateway<StubModule>();

        var northById = await stub.QueryAsync(new ReadSheetsQuery(southSheet, false), For("north"), TestContext.Current.CancellationToken);
        var northList = await stub.QueryAsync(new ReadSheetsQuery(null, false), For("north"), TestContext.Current.CancellationToken);
        var southById = await stub.QueryAsync(new ReadSheetsQuery(southSheet, false), For("south"), TestContext.Current.CancellationToken);
        var southList = await stub.QueryAsync(new ReadSheetsQuery(null, false), For("south"), TestContext.Current.CancellationToken);
        var crossing = await stub.QueryAsync(new ReadSheetsQuery(southSheet, true), For("north"), TestContext.Current.CancellationToken);

        Assert.Empty(northById.Value);
        Assert.Equal(new[] { $"north {_north}", $"north {_otherNorth}" }.Order(), northList.Value.Order());
        Assert.Equal([$"south {_south}"], southById.Value);
        Assert.Equal([$"south {_south}"], southList.Value);
        Assert.Equal([$"south {_south}"], crossing.Value);
    }

    // Each Operation's lease binds its tenant: north's reads none of south's binders, south's, leased after north's from the
    // same pool, reads south's, and an Operation with no tenant reads none.
    [Fact]
    public async Task A_dapper_read_against_the_leases_tenant_reads_only_the_operations_tenants_rows()
    {
        await CreateBindersAsync();
        var stub = Fixture.Gateway<StubModule>();
        var named = new ReadBindersQuery([_north, _otherNorth, _south]);

        var north = await stub.QueryAsync(named, For("north"), TestContext.Current.CancellationToken);
        var south = await stub.QueryAsync(named, For("south"), TestContext.Current.CancellationToken);
        var tenantless = await stub.QueryAsync(named, new OperationContext(GatewayTestExtensions.TestActor), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { _north, _otherNorth }.Order(), north.Value.Order());
        Assert.Equal([_south], south.Value);
        Assert.Empty(tenantless.Value);
    }

    // The lease reads the tenant when it opens, as the EF Core filter does: a factory resolved before its scope's Operation
    // began binds that Operation's tenant, and one in a scope where no Operation began binds none and reads no rows.
    [Fact]
    public async Task A_lease_binds_the_tenant_of_the_operation_begun_when_it_opens_and_none_where_none_began()
    {
        await CreateBindersAsync();

        await using var begun = Fixture.Host.Services.CreateAsyncScope();
        var resolvedFirst = begun.ServiceProvider.GetRequiredService<IReadConnectionFactory>();
        begun.ServiceProvider.BeginOperation(For("south"), typeof(StubModule));
        await using var none = Fixture.Host.Services.CreateAsyncScope();

        var south = await LeaseReadsAsync(resolvedFirst);
        var tenantless = await LeaseReadsAsync(none.ServiceProvider.GetRequiredService<IReadConnectionFactory>());

        Assert.Equal("south", south.Setting);
        Assert.Equal([_south], south.Binders);
        Assert.Equal("", tenantless.Setting);
        Assert.Empty(tenantless.Binders);

        async Task<(string Setting, Guid[] Binders)> LeaseReadsAsync(IReadConnectionFactory connections)
        {
            await using var connection = await connections.OpenAsync(TestContext.Current.CancellationToken);
            var setting = await connection.ExecuteScalarAsync<string>(
                new CommandDefinition("select current_setting('pouryakit.tenant')", cancellationToken: TestContext.Current.CancellationToken)
            );
            var binders = await connection.QueryAsync<Guid>(
                new CommandDefinition(
                    "select id from stub.binders where tenant = current_setting('pouryakit.tenant') and id = any(@Ids)",
                    new { Ids = new[] { _north, _otherNorth, _south } },
                    cancellationToken: TestContext.Current.CancellationToken
                )
            );
            return (setting!, [.. binders]);
        }
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

    private async Task<Guid> CreateCategoryAsync(string tenant)
    {
        var category = Guid.CreateVersion7();
        Assert.True(
            (await Fixture.Gateway<CatalogModule>().SendAsync(new CreateCategoryCommand(category, "Lamps"), For(tenant), TestContext.Current.CancellationToken)).IsSuccess
        );
        return category;
    }

    // Version 1, every created category's.
    private async Task<Result> ForgeAsync(Guid category, ForgedWrite write)
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(For("north"), typeof(CatalogModule));
        return await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new ForgeCategoryCommand(category, 1, write), TestContext.Current.CancellationToken);
    }

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
