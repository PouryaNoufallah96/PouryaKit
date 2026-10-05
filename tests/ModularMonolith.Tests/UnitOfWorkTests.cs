using Catalog;
using Catalog.Categories.Entities;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Categories.ValueObjects;
using Catalog.Products.Features.CreateProduct;
using ModularMonolith.Tests.Stub;
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
}
