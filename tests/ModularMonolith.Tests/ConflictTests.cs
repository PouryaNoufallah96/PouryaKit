using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Errors;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.Features.RenameProduct;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class ConflictTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact(Timeout = 60_000)]
    public async Task Two_operations_that_remove_the_same_product_commit_once_and_the_other_fails_with_a_conflict()
    {
        var (category, _) = await CreateLampAsync();
        Func<IDispatcher, CancellationToken, Task<Result>> remove = async (dispatcher, ct) =>
            await dispatcher.SendAsync(new RemoveProductsCommand(["Lamp"]), ct);

        // Each removal raises a Domain Event whose handler lowers the category's count.
        var (first, second) = await RaceAsync(
            remove,
            remove,
            TestContext.Current.CancellationToken
        );

        Assert.True(first.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(second.Errors));
        // Created, counted the product, then lowered once by the commit that won.
        Assert.Equal(
            [
                [0, 3L],
            ],
            await Fixture.RowsAsync(
                $"select product_count, version from catalog.categories where id = '{category}'"
            )
        );
        Assert.Equal(
            [
                [true, 2L],
            ],
            await Fixture.RowsAsync("select is_deleted, version from catalog.products")
        );
        Assert.Equal(["held first"], await HeldMarksAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task Two_operations_that_rename_the_same_product_commit_once_and_the_other_fails_with_a_conflict()
    {
        var (_, lamp) = await CreateLampAsync();

        // Renaming raises no Domain Event.
        var (first, second) = await RaceAsync(
            async (dispatcher, ct) =>
                await dispatcher.SendAsync(new RenameProductCommand(lamp, "Desk lamp"), ct),
            async (dispatcher, ct) =>
                await dispatcher.SendAsync(new RenameProductCommand(lamp, "Floor lamp"), ct),
            TestContext.Current.CancellationToken
        );

        Assert.True(first.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(second.Errors));
        Assert.Equal(
            [
                ["Desk lamp", 2L],
            ],
            await Fixture.RowsAsync("select name, version from catalog.products")
        );
        Assert.Equal(["held first"], await HeldMarksAsync());
    }

    // Each Operation locks one product's row and renames the other, so their saves wait on each other until PostgreSQL ends
    // the deadlock by aborting one of them. Neither changed what the other loaded: the aborted one fails with the database's
    // error, as any other the Unit of Work does not map, and not with a conflict its caller could resolve by reloading.
    [Fact(Timeout = 60_000)]
    public async Task Two_operations_whose_saves_deadlock_commit_once_and_the_other_fails_with_the_deadlock_not_a_conflict()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (category, lamp) = await CreateLampAsync();
        var desk = Guid.CreateVersion7();
        await Fixture
            .Gateway<CatalogModule>()
            .SendAsync(new CreateProductCommand(desk, category, "Desk"), cancellationToken);
        Hold first = new("held first"),
            second = new("held second");
        var firstRun = RunHeldAsync(LockThenRename(lamp, desk, "Desk 2"), first, cancellationToken);
        var secondRun = RunHeldAsync(
            LockThenRename(desk, lamp, "Lamp 2"),
            second,
            cancellationToken
        );
        await Task.WhenAll(first.Changed.Task, second.Changed.Task).WaitAsync(cancellationToken);

        first.Released.SetResult();
        second.Released.SetResult();
        Task[] runs = [firstRun, secondRun];
        await Task.WhenAll(runs)
            .ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing
                    | ConfigureAwaitOptions.ContinueOnCapturedContext
            );

        Assert.Equal(
            ["committed", $"threw {PostgresErrorCodes.DeadlockDetected}"],
            [.. new[] { Outcome(firstRun), Outcome(secondRun) }.Order(StringComparer.Ordinal)]
        );
        // The commit that won renamed the other Operation's product; the aborted one left nothing, its mark included.
        (string Mark, string[] Names) won = firstRun.IsCompletedSuccessfully
            ? ("held first", ["Desk 2", "Lamp"])
            : ("held second", ["Desk", "Lamp 2"]);
        Assert.Equal([won.Mark], await HeldMarksAsync());
        Assert.Equal(
            won.Names,
            (await Fixture.RowsAsync("select name from catalog.products order by name")).Select(
                row => (string)row[0]
            )
        );

        static Func<IDispatcher, CancellationToken, Task<Result>> LockThenRename(
            Guid locked,
            Guid renamed,
            string name
        ) =>
            async (dispatcher, ct) =>
            {
                await dispatcher.SendAsync(new LockProductCommand(locked), ct);
                return await dispatcher.SendAsync(new RenameProductCommand(renamed, name), ct);
            };

        // The database's error is wrapped by EF Core and its provider, in layers they choose.
        static string Outcome(Task<Result> run)
        {
            if (run.Exception?.InnerException is not { } thrown)
            {
                return run.Result.IsSuccess
                    ? "committed"
                    : string.Join(", ", run.Result.Errors.Select(error => error.Code));
            }

            for (var cause = thrown; cause is not null; cause = cause.InnerException)
            {
                if (cause is PostgresException database)
                {
                    return $"threw {database.SqlState}";
                }
            }

            return $"threw {thrown}";
        }
    }

    [Fact]
    public async Task A_duplicate_product_name_fails_with_the_error_the_module_mapped_and_writes_nothing()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var (category, _) = await CreateLampAsync();

        var created = await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), category, "Lamp"),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(ProductErrors.NameInUse, Assert.Single(created.Errors));
        Assert.Equal(
            [
                ["Lamp"],
            ],
            await Fixture.RowsAsync("select name from catalog.products")
        );
        Assert.Equal(
            1,
            await Fixture.ScalarAsync<int>("select product_count from catalog.categories")
        );
    }

    [Fact]
    public async Task A_removed_products_name_is_free_again()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var (category, _) = await CreateLampAsync();
        await catalog.SendAsync(
            new RemoveProductsCommand(["Lamp"]),
            TestContext.Current.CancellationToken
        );

        var created = await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), category, "Lamp"),
            TestContext.Current.CancellationToken
        );

        Assert.True(created.IsSuccess);
        Assert.Equal(2L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Fact]
    public async Task A_violated_constraint_with_no_mapped_error_stays_an_exception_and_writes_nothing()
    {
        var (category, lamp) = await CreateLampAsync();

        // The product's ID is its primary key, a unique constraint Catalog maps to no Error.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            Fixture
                .Gateway<CatalogModule>()
                .SendAsync(
                    new CreateProductCommand(lamp, category, "Desk"),
                    TestContext.Current.CancellationToken
                )
        );

        Assert.Equal(
            [
                ["Lamp"],
            ],
            await Fixture.RowsAsync("select name from catalog.products")
        );
        Assert.Equal(
            1,
            await Fixture.ScalarAsync<int>("select product_count from catalog.categories")
        );
    }

    private async Task<(Guid Category, Guid Lamp)> CreateLampAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var category = Guid.CreateVersion7();
        var lamp = Guid.CreateVersion7();
        await catalog.SendAsync(
            new CreateCategoryCommand(category, "Lamps"),
            TestContext.Current.CancellationToken
        );
        await catalog.SendAsync(
            new CreateProductCommand(lamp, category, "Lamp"),
            TestContext.Current.CancellationToken
        );
        return (category, lamp);
    }

    private async Task<IEnumerable<string>> HeldMarksAsync() =>
        (
            await Fixture.RowsAsync("select name from catalog.categories where name like 'held %'")
        ).Select(row => (string)row[0]);

    // Runs the changes in two Catalog Operations at once, lets both make them, then lets the first save and commit before
    // the second saves.
    private async Task<(Result First, Result Second)> RaceAsync(
        Func<IDispatcher, CancellationToken, Task<Result>> firstChange,
        Func<IDispatcher, CancellationToken, Task<Result>> secondChange,
        CancellationToken cancellationToken
    )
    {
        Hold first = new("held first"),
            second = new("held second");
        var firstRun = RunHeldAsync(firstChange, first, cancellationToken);
        var secondRun = RunHeldAsync(secondChange, second, cancellationToken);
        await Task.WhenAll(first.Changed.Task, second.Changed.Task).WaitAsync(cancellationToken);

        first.Released.SetResult();
        var firstResult = await firstRun;
        second.Released.SetResult();
        return (firstResult, await secondRun);
    }

    private async Task<Result> RunHeldAsync(
        Func<IDispatcher, CancellationToken, Task<Result>> change,
        Hold hold,
        CancellationToken cancellationToken
    )
    {
        // Yields first, so both Operations start before either reaches its hold.
        await Task.Yield();
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(
            new OperationContext(GatewayTestExtensions.TestActor, AppFixture.TestTenant),
            typeof(CatalogModule)
        );
        return await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new HoldChangeCommand(change, hold), cancellationToken);
    }
}
