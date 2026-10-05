using Catalog;
using Catalog.Categories.Entities;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Infrastructure.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class PersistenceTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task Each_module_has_its_own_schema_and_migrations_history_with_snake_case_names()
    {
        var tables = await Fixture.RowsAsync(
            "select table_schema, table_name from information_schema.tables where table_schema not in ('pg_catalog', 'information_schema') order by 1, 2"
        );
        var history = await Fixture.RowsAsync(
            """select 'catalog', migration_id from catalog."__EFMigrationsHistory" union all select 'stub', migration_id from stub."__EFMigrationsHistory" order by 1, 2"""
        );
        var columns = await Fixture.RowsAsync(
            "select column_name, data_type from information_schema.columns where table_schema = 'catalog' and table_name = 'products' order by ordinal_position"
        );

        Assert.Equal(
            [
                ["catalog", "__EFMigrationsHistory"],
                ["catalog", "categories"],
                ["catalog", "products"],
                ["stub", "__EFMigrationsHistory"],
                ["stub", "note_tallies"],
                ["stub", "notes"],
            ],
            tables
        );
        Assert.Equal(
            ["catalog Initial", "catalog AddCategories", "stub Initial", "stub NoteTallies"],
            history.Select(row => $"{row[0]} {((string)row[1]).Split('_', 2)[1]}")
        );
        // Typed IDs are stored as their Guid values with no ID configuration in the Product mapping.
        Assert.Equal(
            [["id", "uuid"], ["name", "character varying"], ["version", "bigint"], ["category_id", "uuid"]],
            columns
        );
    }

    [Fact]
    public async Task No_module_db_context_has_a_retrying_execution_strategy()
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();

        var strategies = scope
            .ServiceProvider.GetServices<DbContextOptions>()
            .Select(options => (DbContext)scope.ServiceProvider.GetRequiredService(options.ContextType))
            .ToDictionary(dbContext => dbContext.GetType(), dbContext => dbContext.Database.CreateExecutionStrategy());

        Assert.Equal([typeof(CatalogDbContext), typeof(StubDbContext)], strategies.Keys.OrderBy(type => type.Name));
        Assert.All(strategies.Values, strategy => Assert.False(strategy.RetriesOnFailure));
    }

    [Fact]
    public async Task The_aggregate_version_is_written_and_read_through_the_mapping()
    {
        await Fixture
            .Gateway<CatalogModule>()
            .SendAsync(new CreateCategoryCommand(Guid.CreateVersion7(), "Lamps"), TestContext.Current.CancellationToken);
        var written = await Fixture.ScalarAsync<long>("select version from catalog.categories");
        await Fixture.RowsAsync("update catalog.categories set version = 7");

        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        var read = await scope
            .ServiceProvider.GetRequiredService<CatalogDbContext>()
            .Set<Category>()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0L, written);
        Assert.Equal(7L, read.Version);
    }

    [Fact]
    public async Task The_generic_repository_finds_by_id_and_finds_lists_checks_and_counts_by_specification()
    {
        var gateway = Fixture.Gateway<StubModule>();
        foreach (var text in new[] { "a", "b", "a" })
        {
            Assert.True((await gateway.SendAsync(new WriteNoteCommand(text), TestContext.Current.CancellationToken)).IsSuccess);
        }

        var ids = (await Fixture.RowsAsync("select id, text from stub.notes order by id"))
            .Select(row => ((long)row[0], (string)row[1]))
            .ToList();
        var found = (
            await gateway.QueryAsync(new FindNotesQuery(ids[1].Item1, "a"), TestContext.Current.CancellationToken)
        ).Value;

        Assert.All(ids, id => Assert.True(id.Item1 > 0));
        Assert.Equal("b", found.ById?.Text);
        Assert.Equal(ids[0].Item1, found.First?.Id.Value);
        Assert.Equal([ids[0].Item1, ids[2].Item1], found.All.Select(note => note.Id.Value));
        Assert.True(found.Any);
        Assert.Equal(2, found.Count);
    }
}
