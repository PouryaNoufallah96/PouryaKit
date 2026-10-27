using Catalog;
using Catalog.Categories.Errors;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class DomainEventTests(AppFixture app) : HostTest<AppFixture>(app)
{
    #region category-counts-product-test
    [Fact]
    public async Task A_category_counts_a_new_product_in_the_same_commit_and_a_failed_count_leaves_no_product()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var lamps = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(lamps, "Lamps"), TestContext.Current.CancellationToken);

        var created = await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), lamps, "Desk lamp"),
            TestContext.Current.CancellationToken
        );
        var refused = await catalog.SendAsync(
            new CreateProductCommand(Guid.CreateVersion7(), Guid.CreateVersion7(), "Floor lamp"),
            TestContext.Current.CancellationToken
        );

        Assert.True(created.IsSuccess);
        Assert.Equal(CategoryErrors.NotFound, Assert.Single(refused.Errors));
        Assert.Equal([[lamps, 1]], await Fixture.RowsAsync("select id, product_count from catalog.categories"));
        Assert.Equal(["Desk lamp"], (await Fixture.RowsAsync("select name from catalog.products")).Select(row => row[0]));
    }
    #endregion

    [Fact]
    public async Task The_handlers_of_a_domain_event_run_one_at_a_time_in_the_order_of_their_names()
    {
        var log = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteSharedNoteCommand(), TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "AlphaReaction started",
                "AlphaReaction ended",
                "MikeReaction started",
                "MikeReaction ended",
                "ZuluReaction started",
                "ZuluReaction ended",
            ],
            log.Value.Entries
        );
    }

    [Fact]
    public async Task Domain_events_raised_by_handlers_are_handled_until_none_remain_up_to_the_depth_limit()
    {
        var result = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteReactingNoteCommand(Reaction.Chain), TestContext.Current.CancellationToken);

        // The command's note, then one note per round: the last round is the deepest the drain allows.
        string[] expected = ["Chain", .. Enumerable.Range(1, DomainEventDrain.MaxDepth).Select(link => $"link {link}")];
        var texts = (await Fixture.RowsAsync("select text from stub.notes")).Select(row => (string)row[0]);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected.Order(StringComparer.Ordinal), texts.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_domain_event_that_nothing_handles_does_not_stop_the_operation_from_committing()
    {
        var result = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteReactingNoteCommand(Reaction.None), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(["None"], (await Fixture.RowsAsync("select text from stub.notes")).Select(row => row[0]));
    }

    [Fact(Timeout = 60_000)]
    public async Task A_cycle_of_domain_events_throws_at_the_depth_limit_and_leaves_nothing_written()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture
                .Gateway<StubModule>()
                .SendAsync(new WriteReactingNoteCommand(Reaction.Cycle), TestContext.Current.CancellationToken)
        );

        Assert.Contains($"after {DomainEventDrain.MaxDepth} rounds", error.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }

    [Fact]
    public async Task A_failed_handler_result_stops_the_drain_and_the_operation_fails_with_its_errors_leaving_nothing_written()
    {
        var result = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteReactingNoteCommand(Reaction.Refuse), TestContext.Current.CancellationToken);

        // The third handler throws if it runs, and the first one's row is already in the database.
        Assert.Equal(RefusalSecondReaction.Refused, Assert.Single(result.Errors));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }

    // The note also raises a Domain Event a Notification Handler subscribes to, so a commit would store a Notification.
    [Fact]
    public async Task A_nested_command_that_fails_inside_a_handler_rolls_the_operation_back_though_the_handler_succeeds()
    {
        var result = await Fixture.AssertNothingSentAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(new WriteReactingNoteCommand(Reaction.IgnoreNestedFailure), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(WriteNoteThenFailCommandHandler.Rejected, Assert.Single(result.Errors));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }
}
