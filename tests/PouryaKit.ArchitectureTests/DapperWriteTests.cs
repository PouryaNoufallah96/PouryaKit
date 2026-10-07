using Offending;

namespace PouryaKit.ArchitectureTests;

// Rule 4, story 45: aggregates change only through the tracked path, which advances their version and builds their
// Snapshots; SQL writes only tables no aggregate maps, such as a copy of another Module's Snapshots.
public sealed class DapperWriteTests
{
    private static readonly HashSet<string> AggregateTables =
    [
        .. Sample.Models.SelectMany(module => module.Value.SelectMany(model => ModuleRules.AggregateTablesOf(module.Key, model))),
    ];

    [Fact]
    public void The_aggregate_tables_are_read_from_each_modules_model()
    {
        Assert.Superset(
            new HashSet<string>
            {
                "catalog.products",
                "catalog.categories",
                "inventory.stock_items",
                "billing.price_lists",
            },
            AggregateTables
        );
        Assert.DoesNotContain("inventory.catalog_products", AggregateTables);
    }

    [Fact]
    public void No_module_writes_an_aggregate_table_with_sql_and_inventorys_snapshot_copy_is_seen_as_a_write()
    {
        Assert.Empty(Sample.Modules.SelectMany(module => ModuleRules.WritesToAggregateTables(module, AggregateTables)));
        var inventory = Assert.Single(Sample.Modules, module => module.GetName().Name == "Inventory");
        Assert.Equal(
            ["inventory.catalog_products", "inventory.catalog_products", "inventory.product_categories"],
            Il.LiteralsIn(inventory).SelectMany(literal => ModuleRules.TablesWrittenBy(literal.Text)).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void A_module_that_writes_an_aggregate_table_with_sql_is_rejected_with_or_without_the_schema()
    {
        var violations = ModuleRules.WritesToAggregateTables(typeof(OffendingModule).Assembly, AggregateTables);

        Assert.Equal(
            [
                "Offending.RenamingProductsCommandHandler writes the aggregate table catalog.products with SQL: \"update catalog\n.\tproducts set name = 'renamed again'\".",
                "Offending.RenamingProductsCommandHandler writes the aggregate table catalog.products with SQL: \"update catalog.products set name = 'renamed'\".",
                "Offending.RenamingProductsCommandHandler writes the aggregate table categories with SQL: \"delete from categories\".",
            ],
            violations.Order(StringComparer.Ordinal)
        );
    }

    [Theory]
    [InlineData("insert into inventory.catalog_products (product_id) values (@Id) on conflict (product_id) do update set name = excluded.name", "inventory.catalog_products")]
    [InlineData("UPDATE \"Catalog\".\"Products\" SET name = @Name", "catalog.products")]
    [InlineData("merge into stock_items using x on true when matched then update set quantity = 1", "stock_items")]
    [InlineData("select id from catalog.products for update", null)]
    [InlineData("truncate table billing.price_lists", "billing.price_lists")]
    [InlineData("update catalog.\nproducts set name = @Name", "catalog.products")]
    [InlineData("delete from catalog\t. \"Products\" where id = @Id", "catalog.products")]
    [InlineData("update /* c */ catalog.products set name = @Name", "catalog.products")]
    [InlineData("do $$ begin update catalog.products set name = 'x'; end $$", "catalog.products")]
    [InlineData("-- update catalog.products\ninsert into marks (id) values (@Id)", "marks")]
    [InlineData("insert into marks (note) values ('update catalog.products set name = 1')", "marks")]
    public void Sql_writes_are_read_by_the_table_they_name(string sql, string? table)
    {
        Assert.Equal(table is null ? [] : [table], ModuleRules.TablesWrittenBy(sql));
    }
}
