using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs.Tests.Module;

namespace PouryaKit.Cqrs.Tests;

// The dispatcher's side of query caching, with a stand-in cache; the cache's own guarantees are proven against
// PostgreSQL and Redis in ModularMonolith.Tests.
public sealed class QueryCacheTests
{
    [Fact]
    public async Task A_query_reaches_the_cache_after_its_checks_and_never_inside_a_write_unit_of_work()
    {
        await using var host = TestHost.Build(withQueryCache: true);
        using var outside = Begun(host);
        using var inside = Begun(host);
        using var beforeIt = Begun(host);

        await ThingsGateway.GetThing(Dispatcher(outside));
        await ThingsGateway.ReadThingInsideTheUnitOfWork(Dispatcher(inside));
        await ThingsGateway.AuthorizeByRunningAQuery(Dispatcher(beforeIt));

        Assert.Equal(
            [
                "log:Running GetThingQuery",
                "authorize GetThingQuery",
                "validate GetThingQuery",
                "cache GetThingQuery",
                "handle GetThingQuery",
                "log:Succeeded GetThingQuery",
            ],
            Trace(outside)
        );
        Assert.Contains("handle GetThingQuery", Trace(inside));
        Assert.DoesNotContain(Trace(inside), step => step.StartsWith("cache", StringComparison.Ordinal));
        // A query in the outermost command's own checks runs before its Unit of Work starts.
        Assert.Equal(["cache GetThingQuery", "uow:begin"], Trace(beforeIt).Where(step => step is "cache GetThingQuery" or "uow:begin"));
    }

    [Fact]
    public async Task The_tags_of_the_outermost_and_nested_commands_are_evicted_after_the_commit_and_never_after_a_rollback()
    {
        await using var host = TestHost.Build(withQueryCache: true);
        using var committed = Begun(host);
        using var rolledBack = Begun(host);

        await ThingsGateway.RunOuter(Dispatcher(committed), NestedOutcome.Succeeds);
        await ThingsGateway.RunOuter(Dispatcher(rolledBack), NestedOutcome.HandlerFails);

        Assert.Equal(
            ["uow:commit", "evict RunOuterCommand RunNestedCommand", "log:Succeeded RunOuterCommand"],
            Trace(committed).TakeLast(3)
        );
        Assert.Contains("uow:rollback", Trace(rolledBack));
        Assert.DoesNotContain(Trace(rolledBack), step => step.StartsWith("evict", StringComparison.Ordinal));
    }

    private static IServiceScope Begun(ServiceProvider host)
    {
        var scope = host.CreateScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.Person("ada")));
        return scope;
    }

    private static IDispatcher Dispatcher(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<IDispatcher>();

    private static IReadOnlyList<string> Trace(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps;
}
