using Offending;

namespace PouryaKit.ArchitectureTests;

// Spec #115, Multi-tenancy: no query filter reaches Dapper, so in a tenant-scoped Module every SQL read of a
// tenant-scoped table holds each such table to the Operation's tenant at the top level of its WHERE.
public sealed class DapperTenantTests
{
    private static readonly Dictionary<string, string> TenantTables =
        new(Sample.Models.SelectMany(module => module.Value.SelectMany(ModuleRules.TenantTablesOf)));

    [Fact]
    public void The_tenant_scoped_modules_come_from_the_host_and_their_tables_from_each_modules_model()
    {
        Assert.Equal(["Catalog"], Sample.TenantScopedModules.Select(module => module.GetName().Name));
        Assert.Equal(
            [KeyValuePair.Create("catalog.categories", "tenant"), KeyValuePair.Create("catalog.products", "tenant")],
            TenantTables.OrderBy(table => table.Key, StringComparer.Ordinal)
        );
    }

    [Fact]
    public void Every_sql_read_of_a_tenant_scoped_module_holds_each_tenant_scoped_table_to_the_tenant()
    {
        Assert.Empty(Sample.TenantScopedModules.SelectMany(module => ModuleRules.ReadsBeyondTheTenant(module, TenantTables)));
        // The rule judged Catalog's reads: GetProduct's, and ListProducts' SELECT and COUNT.
        var catalog = Assert.Single(Sample.TenantScopedModules);
        Assert.Equal(
            ["GetProductQueryHandler", "ListProductsQueryHandler", "ListProductsQueryHandler"],
            Il.LiteralsIn(catalog)
                .Where(literal => ModuleRules.TenantTablesReadBy(literal.Text, TenantTables).Any())
                .Select(literal => literal.Owner.Name)
                .Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void A_top_level_or_a_union_and_an_unfiltered_join_are_rejected_though_each_names_the_tenant()
    {
        var violations = ModuleRules.ReadsBeyondTheTenant(typeof(OffendingModule).Assembly, TenantTables);

        Assert.Equal(
            [
                "Offending.CrossTenantReadingQueryHandler reads a tenant-scoped table with SQL that does not hold catalog.categories c to @Tenant with a top-level conjunct of its WHERE: \"select p.id from catalog.products p join catalog.categories c on c.id = p.category_id where p.tenant = @Tenant and c.name = @Name\".",
                "Offending.CrossTenantReadingQueryHandler reads a tenant-scoped table with SQL that has an OR at the top level of its WHERE: \"select id from catalog.products where tenant = @Tenant or name = @Name\".",
                "Offending.CrossTenantReadingQueryHandler reads a tenant-scoped table with SQL that has the set operator union: \"select id from catalog.products where tenant = @Tenant union select id from catalog.products where name = @Name\".",
            ],
            violations.Order(StringComparer.Ordinal)
        );
    }

    [Theory]
    [InlineData("select id from catalog.products where tenant = @Tenant", null)]
    [InlineData("select p.id from catalog.products as p where (p.name = @A or p.name = @B) and p.tenant = @Tenant order by p.id", null)]
    [InlineData("select p.id from catalog.products p join categories c on c.id = p.category_id where c.tenant = @Tenant and p.tenant = @Tenant", null)]
    [InlineData("select id from \"Catalog\".\"Products\" where created_at between @From and @To and \"Catalog\".\"Products\".tenant = @Tenant", null)]
    [InlineData("select id from categories where tenant = @Tenant -- or true", null)]
    [InlineData("select id from inventory.stock_items where quantity > 0", null)]
    [InlineData("delete from catalog.products where id = @Id", null)]
    [InlineData("select id from catalog.products where name = @Name and tenant = @Tenant or name = @Other", "has an OR at the top level of its WHERE")]
    [InlineData("select id from catalog.products where created_at between @From and tenant = @Tenant", "does not hold catalog.products to @Tenant with a top-level conjunct of its WHERE")]
    [InlineData("select p.id from catalog.products p where p.tenant = @TenantId", "does not hold catalog.products p to @Tenant with a top-level conjunct of its WHERE")]
    [InlineData("select p.id from catalog.products p where not p.tenant = @Tenant", "does not hold catalog.products p to @Tenant with a top-level conjunct of its WHERE")]
    [InlineData("select p.id from catalog.products p where products.tenant = @Tenant", "does not hold catalog.products p to @Tenant with a top-level conjunct of its WHERE")]
    [InlineData("select p.id from catalog.products p, catalog.categories c where p.tenant = @Tenant", "does not hold catalog.categories c to @Tenant with a top-level conjunct of its WHERE")]
    [InlineData("select p.id from catalog.products p join catalog.categories c on c.id = p.category_id where tenant = @Tenant", "does not hold catalog.products p to @Tenant with a top-level conjunct of its WHERE")]
    [InlineData("select id from catalog.products", "has no WHERE")]
    [InlineData("select id from catalog.products where tenant = @Tenant and id in (select product_id from marks)", "has a subquery")]
    [InlineData("select id from catalog.products where tenant = @Tenant intersect select id from marks", "has the set operator intersect")]
    [InlineData("with t as (select id from catalog.products where tenant = @Tenant) select id from t", "is not a SELECT")]
    [InlineData("from catalog.products p where p.tenant = @Tenant", "is not a SELECT")]
    [InlineData("insert into marks (id) select id from catalog.products where tenant = @Tenant", "is not a SELECT")]
    [InlineData("select id from catalog.products where tenant = @Tenant; select id from marks", "is not one statement")]
    [InlineData("select id from catalog.products where case when true then true else true and tenant = @Tenant and true end", "has a CASE at the top level of its WHERE")]
    [InlineData("select id from catalog.products where $$x and tenant = @Tenant and y$$ <> ''", "holds a dollar sign, a shape the rule does not read")]
    [InlineData("select p.id from (catalog.products p cross join catalog.categories c) where c.tenant = @Tenant", "has a FROM item in parentheses")]
    [InlineData("select id from catalog.products where tenant = @Tenant and \"limit\" = 1 or name = @Name", "has an OR at the top level of its WHERE")]
    [InlineData("select id from catalog.products where tenant = @Tenant and @limit = 1 or true", "has an OR at the top level of its WHERE")]
    [InlineData("TABLE catalog.products", "is not a SELECT")]
    [InlineData("select id from catalog.products where /* outer /* inner */ tenant = @Tenant and */ true", "holds a block comment, a shape the rule does not read")]
    [InlineData("select c.id from catalog.categories c(id, tenant, product_count, version, real_tenant) where c.tenant = @Tenant", "has ( after the FROM item catalog.categories c, a shape the rule does not read")]
    [InlineData("select p.id from catalog.products p where p.tenant = @Tenant order by p.name, p.id limit @Take offset 20", null)]
    [InlineData("select p.id from catalog.products p join catalog.categories c on c.id = p.category_id or true where p.tenant = @Tenant and c.tenant = @Tenant", "has an OR at the top level of the join condition of catalog.categories c")]
    [InlineData("select id from U&\"!0070roducts\" UESCAPE '!' where true", "holds the character &, a shape the rule does not read")]
    public void A_read_of_a_tenant_scoped_table_is_held_to_one_shape(string sql, string? fault)
    {
        Assert.Equal(fault, ModuleRules.TenantReadFault(sql, TenantTables));
    }
}
