using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Offending;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.ArchitectureTests;

// Spec #115, Multi-tenancy: no query filter reaches Dapper, so in a tenant-scoped Module every SQL read of a
// tenant-scoped table holds each such table to the Operation's tenant at the top level of its WHERE, and joins each
// table of a part of a tenant-scoped aggregate up to a table so held (#154). The tenant is the session setting the Dapper
// lease binds, never a value the handler passes (#155).
public sealed class DapperTenantTests
{
    private static readonly Dictionary<string, ModuleRules.TenantTable> TenantTables =
        new(Sample.Models.SelectMany(module => module.Value.SelectMany(ModuleRules.TenantTablesOf)));

    // Catalog's tables and the Offending Module's ledger, a tenant-scoped aggregate with parts, as its model maps them.
    private static readonly Dictionary<string, ModuleRules.TenantTable> WithParts = new([.. TenantTables, .. ModuleRules.TenantTablesOf(LedgerModel())]);

    private static IModel LedgerModel()
    {
        var options = new DbContextOptionsBuilder()
            .UseNpgsql("Host=localhost;Database=architecture-tests", npgsql => npgsql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, "offending"))
            .UseSnakeCaseNamingConvention()
            .Options;
        var contextType = typeof(OffendingModule).Assembly.GetType("Offending.LedgerDbContext", throwOnError: true)!;
        using var context = (DbContext)Activator.CreateInstance(contextType, [options])!;
        return context.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void The_tenant_scoped_modules_come_from_the_host_and_their_tables_from_each_modules_model()
    {
        Assert.Equal(["Catalog", "Ledger"], Sample.TenantScopedModules.Select(module => module.GetName().Name));
        Assert.Equal(
            [
                KeyValuePair.Create("catalog.categories", (string?)"tenant"),
                KeyValuePair.Create("catalog.product_conversations", (string?)"tenant"),
                KeyValuePair.Create("catalog.products", (string?)"tenant"),
            ],
            TenantTables.OrderBy(table => table.Key, StringComparer.Ordinal).Select(table => KeyValuePair.Create(table.Key, table.Value.Tenant))
        );
    }

    [Fact]
    public void A_part_table_is_found_with_its_keys_to_its_holder_as_a_navigation_holds_it_a_part_of_a_part_and_a_derived_part_too()
    {
        // EF Core maps a table split from an entity type of another schema, named with no schema, to the model's default.
        Assert.Equal(
            [
                "books.accounts: tenant",
                "offending.account_notes: id = books.accounts.id",
                "offending.entry_line_notes: id = offending.entry_lines.id",
                "offending.entry_lines: ledger_entry_id = offending.ledger_entries.id",
                "offending.journal_lines: journal_tenant = offending.journals.tenant, journal_id = offending.journals.id",
                "offending.journals: tenant",
                "offending.ledger_entries: ledger_id = offending.ledgers.id",
                "offending.ledgers: tenant",
                "offending.special_entries: id = offending.ledger_entries.id",
            ],
            ModuleRules
                .TenantTablesOf(LedgerModel())
                .Select(table =>
                    $"{table.Key}: {table.Value.Tenant ?? string.Join(", ", table.Value.Holders.SelectMany(key => key.Columns.Select(column => $"{column.Column} = {key.Holder}.{column.HolderColumn}")))}"
                )
                .Order(StringComparer.Ordinal)
        );
    }

    // One cross-check closes the class #152 to #155 found one mapping shape at a time: the tables EF Core filters by tenant,
    // the tables the Dapper rule holds to the tenant, and every table of a tenant-scoped aggregate are the same set.
    [Fact]
    public void The_tables_ef_core_filters_by_tenant_are_the_tables_the_dapper_rule_finds_and_every_table_of_a_tenant_scoped_aggregate()
    {
        IModel[] models = [.. Sample.Models.Values.SelectMany(models => models), LedgerModel()];
        HashSet<string> filtered =
        [
            .. models
                .SelectMany(model => model.GetEntityTypes())
                .Where(entityType => entityType.GetRootType().FindDeclaredQueryFilter(PouryaKitDbContext.TenantFilter) is not null)
                .SelectMany(TablesOf),
        ];
        HashSet<string> found = [.. models.SelectMany(model => ModuleRules.TenantTablesOf(model).Select(table => table.Key))];
        HashSet<string> held = [.. models.SelectMany(TenantScopedAggregateTypes).SelectMany(TablesOf)];

        Assert.Equal(
            [
                "books.accounts",
                "catalog.categories",
                "catalog.product_conversations",
                "catalog.products",
                "offending.account_notes",
                "offending.entry_line_notes",
                "offending.entry_lines",
                "offending.journal_lines",
                "offending.journals",
                "offending.ledger_entries",
                "offending.ledgers",
                "offending.special_entries",
            ],
            held.Order(StringComparer.Ordinal)
        );
        Assert.Empty(
            filtered
                .Except(found)
                .Select(table => $"{table}: EF Core filters it by tenant, the Dapper rule does not find it")
                .Concat(found.Except(filtered).Select(table => $"{table}: the Dapper rule finds it, EF Core does not filter it by tenant"))
                .Concat(held.Except(filtered).Except(found).Select(table => $"{table}: a table of a tenant-scoped aggregate in neither set"))
                .Order(StringComparer.Ordinal)
        );
    }

    // Each ITenantScoped type and every type a navigation of one holds, at any level, short of the parts of another
    // aggregate, with the types derived from each: walked down from the roots, where the convention climbs up from a part.
    private static HashSet<IEntityType> TenantScopedAggregateTypes(IModel model)
    {
        HashSet<IEntityType> walked = [];
        Queue<IEntityType> next = new(model.GetEntityTypes().Where(entityType => entityType.ClrType.IsAssignableTo(typeof(ITenantScoped))));
        while (next.TryDequeue(out var entityType))
        {
            if (!walked.Add(entityType))
            {
                continue;
            }

            foreach (var derived in entityType.GetDirectlyDerivedTypes())
            {
                next.Enqueue(derived);
            }

            if (entityType.ClrType.IsAssignableTo(typeof(ITenantScoped)) || !entityType.ClrType.IsAssignableTo(typeof(IAggregateRoot)))
            {
                foreach (var navigation in entityType.GetNavigations().Where(navigation => !navigation.IsOnDependent))
                {
                    next.Enqueue(navigation.TargetEntityType);
                }
            }
        }

        return walked;
    }

    // The store tables EF Core maps an entity type to: its own, a base type's under TPT, and each table split from it.
    private static IEnumerable<string> TablesOf(IEntityType entityType) =>
        entityType.GetTableMappings().Select(mapping => $"{mapping.Table.Schema ?? entityType.Model.GetDefaultSchema()}.{mapping.Table.Name}");

    [Fact]
    public void A_direct_read_of_a_part_table_and_a_join_to_its_root_without_the_tenant_conjunct_are_rejected()
    {
        var violations = ModuleRules.ReadsBeyondTheTenant(typeof(OffendingModule).Assembly, WithParts);

        Assert.Equal(
            [
                "Offending.PartReadingQueryHandler reads a tenant-scoped table with SQL that does not join the part offending.ledger_entries e on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant'): \"select e.amount from offending.ledger_entries e join offending.ledgers l on l.id = e.ledger_id where l.name = @Name\".",
                "Offending.PartReadingQueryHandler reads a tenant-scoped table with SQL that does not join the part offending.ledger_entries on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant'): \"select amount from offending.ledger_entries where ledger_id = @Ledger\".",
            ],
            violations.Where(violation => violation.StartsWith("Offending.PartReadingQueryHandler ", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void Every_sql_read_of_a_tenant_scoped_module_holds_each_tenant_scoped_table_to_the_tenant()
    {
        Assert.Empty(Sample.TenantScopedModules.SelectMany(module => ModuleRules.ReadsBeyondTheTenant(module, TenantTables)));
        // The rule judged Catalog's reads: GetProduct's, and ListProducts' SELECT and COUNT; a migration's SQL is not a read.
        var catalog = Assert.Single(Sample.TenantScopedModules, module => module.GetName().Name == "Catalog");
        Assert.Equal(
            ["GetProductQueryHandler", "ListProductsQueryHandler", "ListProductsQueryHandler"],
            Il.LiteralsIn(catalog)
                .Where(literal => !literal.Owner.IsAssignableTo(typeof(Migration)) && ModuleRules.TenantTablesReadBy(literal.Text, TenantTables).Any())
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
                "Offending.CrossTenantReadingQueryHandler reads a tenant-scoped table with SQL that does not hold catalog.categories c to the tenant with a top-level conjunct of its WHERE that is exactly c.tenant = current_setting('pouryakit.tenant'): \"select p.id from catalog.products p join catalog.categories c on c.id = p.category_id where p.tenant = current_setting('pouryakit.tenant') and c.name = @Name\".",
                "Offending.CrossTenantReadingQueryHandler reads a tenant-scoped table with SQL that does not hold catalog.products to the tenant with a top-level conjunct of its WHERE that is exactly products.tenant = current_setting('pouryakit.tenant'): \"select id from catalog.products where tenant = @Tenant\".",
                "Offending.CrossTenantReadingQueryHandler reads a tenant-scoped table with SQL that has an OR at the top level of its WHERE: \"select id from catalog.products where tenant = current_setting('pouryakit.tenant') or name = @Name\".",
                "Offending.CrossTenantReadingQueryHandler reads a tenant-scoped table with SQL that has the set operator union: \"select id from catalog.products where tenant = current_setting('pouryakit.tenant') union select id from catalog.products where name = @Name\".",
            ],
            violations.Order(StringComparer.Ordinal)
        );
    }

    [Theory]
    [InlineData("select id from catalog.products where tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select p.id from catalog.products as p where (p.name = @A or p.name = @B) and p.tenant = current_setting('pouryakit.tenant') order by p.id", null)]
    [InlineData("select p.id from catalog.products p join categories c on c.id = p.category_id where c.tenant = current_setting('pouryakit.tenant') and p.tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select id from \"catalog\".\"products\" where created_at between @From and @To and \"catalog\".\"products\".tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select id from categories where tenant = current_setting('pouryakit.tenant') -- or true", null)]
    [InlineData("select id from catalog.products where tenant = current_setting('pouryakit.tenant') -- x\r or true", "has an OR at the top level of its WHERE")]
    [InlineData("select id from inventory.stock_items where quantity > 0", null)]
    [InlineData("delete from catalog.products where id = @Id", null)]
    [InlineData("select id from catalog.products where name = @Name and tenant = current_setting('pouryakit.tenant') or name = @Other", "has an OR at the top level of its WHERE")]
    [InlineData("select id from catalog.products where created_at between @From and tenant = current_setting('pouryakit.tenant')", "does not hold catalog.products to the tenant with a top-level conjunct of its WHERE that is exactly products.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where p.tenant = @Tenant", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where p.tenant = @TenantId", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where p.tenant = current_setting('pouryakit.tenant', true)", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where p.tenant = current_setting('pouryakit.owner')", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where p.tenant = current_setting(@Setting)", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where p.tenant = lower(current_setting('pouryakit.tenant'))", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where current_setting('pouryakit.tenant') = p.tenant", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where not p.tenant = current_setting('pouryakit.tenant')", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p where products.tenant = current_setting('pouryakit.tenant')", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select \"P\".id from catalog.products p join catalog.products \"P\" on true where p.tenant = current_setting('pouryakit.tenant')", "does not hold catalog.products \"P\" to the tenant with a top-level conjunct of its WHERE that is exactly \"P\".tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select \"P\".id from catalog.products p join catalog.products \"P\" on true where p.tenant = current_setting('pouryakit.tenant') and \"P\".tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select p.id from catalog.products p, catalog.categories c where p.tenant = current_setting('pouryakit.tenant')", "does not hold catalog.categories c to the tenant with a top-level conjunct of its WHERE that is exactly c.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select p.id from catalog.products p join catalog.categories c on c.id = p.category_id where tenant = current_setting('pouryakit.tenant')", "does not hold catalog.products p to the tenant with a top-level conjunct of its WHERE that is exactly p.tenant = current_setting('pouryakit.tenant')")]
    [InlineData("select id from catalog.products", "has no WHERE")]
    [InlineData("select id from catalog.products where tenant = current_setting('pouryakit.tenant') and id in (select product_id from marks)", "has a subquery")]
    [InlineData("select id from catalog.products where tenant = current_setting('pouryakit.tenant') intersect select id from marks", "has the set operator intersect")]
    [InlineData("with t as (select id from catalog.products where tenant = current_setting('pouryakit.tenant')) select id from t", "is not a SELECT")]
    [InlineData("from catalog.products p where p.tenant = current_setting('pouryakit.tenant')", "is not a SELECT")]
    [InlineData("insert into marks (id) select id from catalog.products where tenant = current_setting('pouryakit.tenant')", "is not a SELECT")]
    [InlineData("select id from catalog.products where tenant = current_setting('pouryakit.tenant'); select id from marks", "is not one statement")]
    [InlineData("select id from catalog.products where case when true then true else true and tenant = current_setting('pouryakit.tenant') and true end", "has a CASE at the top level of its WHERE")]
    [InlineData("select id from catalog.products where $$x and tenant = current_setting('pouryakit.tenant') and y$$ <> ''", "holds a dollar sign, a shape the rule does not read")]
    [InlineData("select p.id from (catalog.products p cross join catalog.categories c) where c.tenant = current_setting('pouryakit.tenant')", "has a FROM item in parentheses")]
    [InlineData("select id from catalog.products where tenant = current_setting('pouryakit.tenant') and \"limit\" = 1 or name = @Name", "has an OR at the top level of its WHERE")]
    [InlineData("select id from catalog.products where tenant = current_setting('pouryakit.tenant') and @limit = 1 or true", "has an OR at the top level of its WHERE")]
    [InlineData("TABLE catalog.products", "is not a SELECT")]
    [InlineData("select id from catalog.products where /* outer /* inner */ tenant = current_setting('pouryakit.tenant') and */ true", "holds a block comment, a shape the rule does not read")]
    [InlineData("select c.id from catalog.categories c(id, tenant, product_count, version, real_tenant) where c.tenant = current_setting('pouryakit.tenant')", "has ( after the FROM item catalog.categories c, a shape the rule does not read")]
    [InlineData("select p.id from catalog.products p where p.tenant = current_setting('pouryakit.tenant') order by p.name, p.id limit @Take offset 20", null)]
    [InlineData("select p.id from catalog.products p join catalog.categories c on c.id = p.category_id or true where p.tenant = current_setting('pouryakit.tenant') and c.tenant = current_setting('pouryakit.tenant')", "has an OR at the top level of the join condition of catalog.categories c")]
    [InlineData("select id from U&\"!0070roducts\" UESCAPE '!' where true", "holds the character &, a shape the rule does not read")]
    [InlineData("select e.amount from offending.ledger_entries e join offending.ledgers l on l.id = e.ledger_id where l.tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select l.name, e.amount from offending.ledgers l left join offending.ledger_entries e on e.ledger_id = l.id and e.amount > 0 where l.tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select x.text from offending.entry_lines x join offending.ledger_entries e on e.id = x.ledger_entry_id join offending.ledgers l on l.id = e.ledger_id where l.tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select e.amount from offending.ledger_entries e, offending.ledgers l where e.ledger_id = l.id and l.tenant = current_setting('pouryakit.tenant')", "does not join the part offending.ledger_entries e on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant')")]
    [InlineData("select e.amount from offending.ledger_entries e join offending.ledgers l on l.id = ledger_id where l.tenant = current_setting('pouryakit.tenant')", "does not join the part offending.ledger_entries e on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant')")]
    [InlineData("select e.amount from offending.ledger_entries e join offending.ledgers l on l.id = e.id where l.tenant = current_setting('pouryakit.tenant')", "does not join the part offending.ledger_entries e on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant')")]
    [InlineData("select e.amount from offending.ledger_entries e join catalog.products p on p.id = e.ledger_id where p.tenant = current_setting('pouryakit.tenant')", "does not join the part offending.ledger_entries e on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant')")]
    [InlineData("select x.text from offending.entry_lines x join offending.ledgers l on true left join offending.ledger_entries e on e.id = x.ledger_entry_id and e.ledger_id = l.id where l.tenant = current_setting('pouryakit.tenant')", "does not join the part offending.entry_lines x on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant')")]
    [InlineData("select note from offending.special_entries where id = @Id", "does not join the part offending.special_entries on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant')")]
    [InlineData("select s.note from offending.special_entries s join offending.ledger_entries e on e.id = s.id join offending.ledgers l on l.id = e.ledger_id where l.tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select note from offending.account_notes where id = @Id", "does not join the part offending.account_notes on its key to its holder up to books.accounts held to current_setting('pouryakit.tenant')")]
    [InlineData("select note from offending.entry_line_notes where id = @Id","does not join the part offending.entry_line_notes on its key to its holder up to offending.ledgers held to current_setting('pouryakit.tenant')")]
    [InlineData("select n.note from offending.entry_line_notes n join offending.entry_lines x on x.id = n.id join offending.ledger_entries e on e.id = x.ledger_entry_id join offending.ledgers l on l.id = e.ledger_id where l.tenant = current_setting('pouryakit.tenant')", null)]
    [InlineData("select x.text from offending.journal_lines x join offending.journals j on j.id = x.journal_id where j.tenant = current_setting('pouryakit.tenant')","does not join the part offending.journal_lines x on its key to its holder up to offending.journals held to current_setting('pouryakit.tenant')")]
    [InlineData("select x.text from offending.journal_lines x join offending.journals j on j.id = x.journal_id and j.tenant = x.journal_tenant where j.tenant = current_setting('pouryakit.tenant')", null)]
    public void A_read_of_a_tenant_scoped_table_is_held_to_one_shape(string sql, string? fault)
    {
        Assert.Equal(fault, ModuleRules.TenantReadFault(sql, WithParts));
    }
}
