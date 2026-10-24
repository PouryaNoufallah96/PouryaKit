using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Infrastructure.Data.Context;
using Catalog.Products.Data.Repository;
using Catalog.Products.Entities;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Catalog.Products.Features.ListProducts;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;
using PouryaKit.Validation;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// Catalog is tenant-scoped (ticket #116): its products and categories belong to the tenant whose Operation created them,
// every EF Core read sees only the Operation's tenant's rows, and every command and query of it needs a tenant.
public sealed class TenancyTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_tenant_reads_writes_and_removes_none_of_another_tenants_products_through_the_gateway()
    {
        var north = await CreateProductAsync("north", "Lamp");
        var south = await CreateProductAsync("south", "Lamp");
        var catalog = Fixture.Gateway<CatalogModule>();

        var got = await catalog.QueryAsync(new GetProductQuery(south), For("north"), TestContext.Current.CancellationToken);
        var listed = await catalog.QueryAsync(new ListProductsQuery(new PageRequest()), For("north"), TestContext.Current.CancellationToken);
        var renamed = await catalog.SendAsync(new RenameProductCommand(south, "Shade"), For("north"), TestContext.Current.CancellationToken);
        var removed = await catalog.SendAsync(new RemoveProductsCommand(["Lamp"]), For("north"), TestContext.Current.CancellationToken);

        Assert.Equal(ProductErrors.NotFound, Assert.Single(got.Errors));
        Assert.Equal([north], listed.Value.Items.Select(item => item.Id.Value));
        Assert.Equal(ProductErrors.NotFound, Assert.Single(renamed.Errors));
        Assert.Equal(1, removed.Value);
        Assert.Equal(
            [["north", "Lamp", true], ["south", "Lamp", false]],
            await Fixture.RowsAsync("select tenant, name, is_deleted from catalog.products order by tenant")
        );
    }

    [Fact]
    public async Task Over_http_a_tenant_gets_and_lists_none_of_another_tenants_products()
    {
        var north = await CreateProductAsync("north", "Lamp");
        var south = await CreateProductAsync("south", "Lamp");
        using var northern = Fixture.Client("ada", "north");
        using var southern = Fixture.Client("bob", "south");

        var foreign = await northern.GetAsync($"/catalog/products/{south}", TestContext.Current.CancellationToken);
        var own = await southern.GetFromJsonAsync<JsonElement>($"/catalog/products/{south}", TestContext.Current.CancellationToken);
        var page = await northern.GetFromJsonAsync<JsonElement>("/catalog/products?size=10", TestContext.Current.CancellationToken);

        await ProblemAsync(foreign, HttpStatusCode.NotFound);
        Assert.Equal(south, own.GetProperty("id").GetGuid());
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(north, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    // Only the named filter is set aside: a removed product stays out. The filter reads the tenant when each query runs,
    // so a DbContext made before its scope's Operation began reads that Operation's tenant, and one in a scope where no
    // Operation began, as the migration tool's, builds and reads no tenant's rows.
    [Fact]
    public async Task A_read_that_sets_the_tenant_filter_aside_crosses_tenants_and_no_other_read_does()
    {
        var north = await CreateProductAsync("north", "Lamp");
        var south = await CreateProductAsync("south", "Lamp");
        await CreateProductAsync("south", "Desk");
        Assert.Equal(1, (await Fixture.Gateway<CatalogModule>().SendAsync(new RemoveProductsCommand(["Desk"]), For("south"), TestContext.Current.CancellationToken)).Value);

        List<Guid> read, crossing, unbegun;
        await using (var scope = Fixture.Host.Services.CreateAsyncScope())
        {
            var products = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().Set<Product>();
            scope.ServiceProvider.BeginOperation(For("north"), typeof(CatalogModule));
            read = await products.Select(product => product.Id.Value).ToListAsync(TestContext.Current.CancellationToken);
            crossing = await products
                .IgnoreQueryFilters([PouryaKitDbContext.TenantFilter])
                .OrderBy(product => product.Tenant)
                .Select(product => product.Id.Value)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = Fixture.Host.Services.CreateAsyncScope())
        {
            unbegun = await scope
                .ServiceProvider.GetRequiredService<CatalogDbContext>()
                .Set<Product>()
                .Select(product => product.Id.Value)
                .ToListAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("PouryaKit.Tenant", PouryaKitDbContext.TenantFilter);
        Assert.Equal([north], read);
        Assert.Equal([north, south], crossing);
        Assert.Empty(unbegun);
    }

    // The requirement runs before the validator, which would refuse the blank name, and the handler, which would answer
    // NotFound; over HTTP it is a 403 problem response.
    [Fact]
    public async Task An_operation_without_a_tenant_fails_forbidden_before_its_validator_and_handler_run()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        using var tenantless = Fixture.Client("ada");

        var created = await catalog.SendAsync(new CreateCategoryCommand(Guid.CreateVersion7(), "Lamps"), For(null), TestContext.Current.CancellationToken);
        var invalid = await catalog.SendAsync(new CreateCategoryCommand(Guid.CreateVersion7(), " "), For(null), TestContext.Current.CancellationToken);
        var read = await catalog.QueryAsync(new GetProductQuery(Guid.CreateVersion7()), For(null), TestContext.Current.CancellationToken);
        var listed = await tenantless.GetAsync("/catalog/products", TestContext.Current.CancellationToken);

        Assert.Equal(TenantErrors.Required, Assert.Single(created.Errors));
        Assert.Equal(TenantErrors.Required, Assert.Single(invalid.Errors));
        Assert.Equal(TenantErrors.Required, Assert.Single(read.Errors));
        var problem = await ProblemAsync(listed, HttpStatusCode.Forbidden);
        Assert.Equal(
            [TenantErrors.Required.Code],
            problem.GetProperty("errors").EnumerateArray().Select(error => error.GetProperty("code").GetString())
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.categories"));
    }

    // Stub asks Catalog through ICatalogProducts, which continues the asking Operation's context inside it: a trusted
    // continuation, which a permission check would not authorize again. The tenant requirement still runs for it.
    [Fact]
    public async Task A_trusted_continuation_into_a_tenant_scoped_module_runs_for_its_tenant_and_is_refused_without_one()
    {
        var lamp = await CreateProductAsync("north", "Lamp");
        var stub = Fixture.Gateway<StubModule>();

        var noted = await stub.SendAsync(new NoteProductCommand(lamp), For("north"), TestContext.Current.CancellationToken);
        var foreign = await stub.SendAsync(new NoteProductCommand(lamp), For("south"), TestContext.Current.CancellationToken);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stub.SendAsync(new NoteProductCommand(lamp), For(null), TestContext.Current.CancellationToken)
        );

        Assert.True(noted.IsSuccess);
        Assert.Equal(NoteProductCommandHandler.UnknownProduct, Assert.Single(foreign.Errors));
        Assert.Equal("Catalog could not answer FindProductAsync: Tenant.Required.", refused.Message);
        Assert.True(Fixture.Host.Services.GetRequiredService<CatalogQuestionContexts>().Seen[^1].IsTrustedContinuation);
        Assert.Equal(["Lamp"], (await Fixture.RowsAsync("select text from stub.notes")).Select(row => row[0]));
    }

    // An application without Modules declares it on its own services, naming its Feature assemblies: here Catalog's,
    // composed as the whole application.
    [Fact]
    public async Task An_application_without_modules_declared_tenant_scoped_holds_its_operations_to_their_tenant()
    {
        await using var services = CatalogAsTheApplication();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.True((await SendAsync(services, "north", new CreateCategoryCommand(category, "Lamps"))).IsSuccess);
        Assert.True((await SendAsync(services, "north", new CreateProductCommand(product, category, "Lamp"))).IsSuccess);

        var foreign = await SendAsync(services, "south", new RenameProductCommand(product, "Shade"));
        var tenantless = await SendAsync(services, null, new CreateCategoryCommand(Guid.CreateVersion7(), "Desks"));
        var own = await SendAsync(services, "north", new RenameProductCommand(product, "Shade"));

        Assert.Equal(ProductErrors.NotFound, Assert.Single(foreign.Errors));
        Assert.Equal(TenantErrors.Required, Assert.Single(tenantless.Errors));
        Assert.True(own.IsSuccess);
        Assert.Equal([["north", "Lamps"]], await Fixture.RowsAsync("select tenant, name from catalog.categories"));
        Assert.Equal("Shade", await Fixture.ScalarAsync<string>("select name from catalog.products"));
    }

    // The seam later Kit registrations read: Catalog's Composition Root declared it, the Stub's did not, and neither did
    // the application's own services.
    [Fact]
    public void The_host_records_which_modules_are_tenant_scoped()
    {
        var composition = Fixture.Host.Services.GetRequiredService<HostComposition>();

        Assert.True(composition.IsTenantScoped(typeof(CatalogModule)));
        Assert.False(composition.IsTenantScoped(typeof(StubModule)));
        Assert.False(composition.IsTenantScoped(null));
    }

    // A product or category written before Catalog was tenant-scoped has no tenant to recover (ticket #150), so the
    // migration that adds the tenant refuses a table that holds one, on a database of its own, and applies to an empty one.
    [Theory]
    [InlineData("insert into catalog.products (id, name, version) values (gen_random_uuid(), 'Lamp', 1)")]
    [InlineData("insert into catalog.categories (id, name, product_count, version) values (gen_random_uuid(), 'Lighting', 0, 1)")]
    public async Task Catalogs_migration_that_adds_the_tenant_refuses_a_table_holding_a_row_with_no_tenant(string insert)
    {
        var database = $"tenants_{Guid.CreateVersion7():N}";
        await Fixture.RowsAsync($"create database {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(Fixture.ConnectionString) { Database = database }.ConnectionString;
        await using var host = AppFixture.ComposeHost("Development", connectionString, new RecordedLogs());
        await using var scope = host.Services.CreateAsyncScope();
        var migrator = scope.ServiceProvider.GetRequiredService<CatalogDbContext>().GetService<IMigrator>();
        await migrator.MigrateAsync("20261007120000_IdempotencyKeys", TestContext.Current.CancellationToken);
        await Sql.RowsAsync(connectionString, insert);

        var refused = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(
            "Catalog holds products or categories that kept no tenant, and their tenant cannot be recovered, so this migration refuses to guess one.",
            refused.MessageText
        );
        await Sql.RowsAsync(connectionString, "delete from catalog.products; delete from catalog.categories");
        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(
            [["categories"], ["products"]],
            await Sql.RowsAsync(
                connectionString,
                "select table_name from information_schema.columns where table_schema = 'catalog' and table_name in ('products', 'categories') and column_name = 'tenant' order by table_name"
            )
        );
    }

    private static OperationContext For(string? tenant) => new(GatewayTestExtensions.TestActor, tenant);

    private async Task<Guid> CreateProductAsync(string tenant, string name)
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        Assert.True((await catalog.SendAsync(new CreateCategoryCommand(category, $"{name}s"), For(tenant), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await catalog.SendAsync(new CreateProductCommand(product, category, name), For(tenant), TestContext.Current.CancellationToken)).IsSuccess);
        return product;
    }

    private ServiceProvider CatalogAsTheApplication()
    {
        var catalog = typeof(CatalogModule).Assembly;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new("ConnectionStrings:app", Fixture.ConnectionString)])
                .Build()
        );
        services.AddPouryaKitTenancy(catalog);
        services.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        services.AddPouryaKitCqrs(catalog);
        services.AddPouryaKitValidation(catalog);
        services.AddScoped<IProductRepository, ProductRepository>();
        return services.BuildServiceProvider();
    }

    private static async Task<Result> SendAsync(IServiceProvider services, string? tenant, Command command)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(For(tenant));
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(command, TestContext.Current.CancellationToken);
    }

    private static async Task<Result> SendAsync<TResponse>(IServiceProvider services, string? tenant, Command<TResponse> command)
        where TResponse : notnull
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(For(tenant));
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(command, TestContext.Current.CancellationToken);
    }
}
