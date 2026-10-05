using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

public sealed class GatewayTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task Each_operation_runs_in_its_own_scope_that_is_disposed_when_it_returns()
    {
        var gateway = Fixture.Gateway<StubModule>();

        var first = (await gateway.QueryAsync(new ProbeScopeQuery(), TestContext.Current.CancellationToken)).Value;
        var second = (await gateway.QueryAsync(new ProbeScopeQuery(), TestContext.Current.CancellationToken)).Value;

        Assert.NotSame(first.DbContext, second.DbContext);
        Assert.NotSame(first.Operation, second.Operation);
        Assert.Throws<ObjectDisposedException>(() => first.DbContext.Model);
        Assert.Throws<ObjectDisposedException>(() => second.DbContext.Model);
    }

    [Fact]
    public async Task Each_modules_command_commits_through_its_own_db_context()
    {
        var category = Guid.CreateVersion7();

        var created = await Fixture
            .Gateway<CatalogModule>()
            .SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);
        var written = await Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteNoteCommand("hello"), TestContext.Current.CancellationToken);

        Assert.True(created.IsSuccess);
        Assert.True(written.IsSuccess);
        Assert.Equal(category, await Fixture.ScalarAsync<Guid>("select id from catalog.categories"));
        Assert.Equal("hello", await Fixture.ScalarAsync<string>("select text from stub.notes"));
    }

    [Fact]
    public async Task A_command_sent_outside_every_gateway_finds_no_unit_of_work()
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.SendAsync(new WriteNoteCommand("hello"), TestContext.Current.CancellationToken)
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }
}
