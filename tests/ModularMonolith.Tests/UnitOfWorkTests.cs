using Catalog;
using Catalog.Categories.Entities;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Categories.ValueObjects;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Host;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class UnitOfWorkTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_command_that_raises_no_domain_event_is_committed_though_its_handler_never_saves()
    {
        var id = Guid.CreateVersion7();
        Assert.Empty(Category.Create(new CategoryId(id), "Lamps").Value.DomainEvents);

        var created = await Fixture
            .Gateway<CatalogModule>()
            .SendAsync(new CreateCategoryCommand(id, " Lamps "), TestContext.Current.CancellationToken);

        Assert.Equal(id, created.Value);
        var row = Assert.Single(await Fixture.RowsAsync("select id, name from catalog.categories"));
        Assert.Equal([id, "Lamps"], row);
    }

    [Fact]
    public async Task A_failed_result_returned_after_writing_leaves_nothing_written()
    {
        var result = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteNoteThenFailCommand("draft", Ending.Fail), TestContext.Current.CancellationToken);

        Assert.Equal(WriteNoteThenFailCommandHandler.Rejected, Assert.Single(result.Errors));
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }

    [Fact]
    public async Task An_exception_thrown_after_writing_leaves_nothing_written()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fixture
                .Gateway<StubModule>()
                .SendAsync(new WriteNoteThenFailCommand("draft", Ending.Throw), TestContext.Current.CancellationToken)
        );

        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }

    [Fact]
    public async Task A_command_that_fails_validation_returns_every_failure_and_writes_nothing()
    {
        var result = await Fixture
            .Gateway<CatalogModule>()
            .SendAsync(new CreateProductCommand(Guid.Empty, Guid.Empty, ""), TestContext.Current.CancellationToken);

        Assert.Equal(["CategoryId", "Name", "ProductId"], result.Errors.Select(error => error.Target).Order());
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    // The outer handler posts a note, whose Notification the commit would store, then ignores its nested command's
    // failure and returns success: the Operation still fails, and neither note nor Notification is left.
    [Theory]
    [InlineData(NestedFailure.Handler, "Note.NestedRefused")]
    [InlineData(NestedFailure.Validation, "NotEqualValidator")]
    [InlineData(NestedFailure.Authorization, "Note.NestedForbidden")]
    public async Task A_nested_command_that_fails_rolls_back_the_operations_data_and_outbox_though_the_outer_handler_ignores_it(
        NestedFailure failure,
        string errorCode
    )
    {
        var text = $"note {Guid.CreateVersion7()}";

        var result = await Fixture.AssertNothingSentAsync(
            () => Fixture.Gateway<StubModule>().SendAsync(new PostNoteThenNestCommand(text, failure), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.Equal(errorCode, Assert.Single(result.Errors).Code);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text in ('{text}', 'nested {text}')"));
    }

    [Fact]
    public async Task The_same_operation_with_a_nested_command_that_succeeds_commits_both_notes_and_stores_the_notification()
    {
        var text = $"note {Guid.CreateVersion7()}";

        var (result, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () =>
                Fixture
                    .Gateway<StubModule>()
                    .SendAsync(new PostNoteThenNestCommand(text, NestedFailure.None), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.True(result.IsSuccess);
        Assert.Contains(
            new Delivery(
                MessageNames.SubscriptionNameOf(typeof(RecordPostedNoteNotificationHandler), typeof(NotePostedDomainEvent)),
                MessageNames.WireNameOf(typeof(NotePostedDomainEvent)),
                false
            ),
            deliveries
        );
        Assert.Equal(2L, await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text in ('{text}', 'nested {text}')"));
    }

    // A Receipt or a Snapshot's version commits with its delivery's effect only in the Unit of Work's transaction; a scope
    // that runs no command has none, so either is refused there rather than committed on its own.
    [Fact]
    public async Task A_receipt_or_a_snapshot_version_recorded_where_no_unit_of_work_is_active_is_refused_and_nothing_is_kept()
    {
        const string subscription = "tests.no-unit-of-work";
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(GatewayTestExtensions.TestActor), typeof(StubModule));
        var receipts = scope.ServiceProvider.GetRequiredService<IReceipts>();

        var receipt = await Record.ExceptionAsync(() => receipts.RecordAsync(subscription, Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        var version = await Record.ExceptionAsync(() => receipts.RecordVersionAsync(subscription, "aggregate", 1, TestContext.Current.CancellationToken));

        Assert.Equal(
            [[0L, 0L]],
            await Fixture.RowsAsync(
                $"select (select count(*) from stub.pouryakit_receipts where subscription = '{subscription}'), (select count(*) from stub.pouryakit_snapshot_versions where subscription = '{subscription}')"
            )
        );
        Assert.Equal("No Unit of Work is active, so there is no transaction to keep Receipts in.", Assert.IsType<InvalidOperationException>(receipt).Message);
        Assert.Equal(
            "No Unit of Work is active, so there is no transaction to keep the versions of the Snapshots its handlers applied in.",
            Assert.IsType<InvalidOperationException>(version).Message
        );
    }

    // The Unit of Work writes through the Outbox the Host's IOutbox marks: one instance per scope.
    [Fact]
    public async Task A_scope_resolves_one_outbox_for_its_unit_of_work_and_the_hosts_outbox_marker()
    {
        await using var host = ModularMonolithHost.Build(
            ModularMonolithHost.CreateBuilder(["--environment", "Development", $"--ConnectionStrings:app={Fixture.ConnectionString}"])
        );
        await using var scope = host.Services.CreateAsyncScope();

        Assert.Same(scope.ServiceProvider.GetRequiredService<IUnitOfWorkOutbox>(), scope.ServiceProvider.GetRequiredService<IOutbox>());
    }
}
