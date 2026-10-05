using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs.Tests.Module;

namespace PouryaKit.Cqrs.Tests;

public sealed class ReadingStepTests
{
    [Fact]
    public async Task A_step_that_only_reads_refuses_commands_and_runs_queries_until_it_closes_and_closes_only_itself()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();
        var operation = scope.ServiceProvider.GetRequiredService<Operation>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var outer = operation.OnlyReading("the outer step");
        var inner = operation.OnlyReading("the inner step");
        var refusedInside = await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.Ping(dispatcher));
        var read = await ThingsGateway.GetThing(dispatcher);
        // Closed out of order: the inner step stays open.
        outer.Dispose();
        outer.Dispose();
        var mayChangeInInner = operation.MayChangeState;
        var refusedInInner = await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.Ping(dispatcher));
        inner.Dispose();
        var mayChangeAfter = operation.MayChangeState;

        Assert.Equal(
            "A command was sent from the inner step, a step that only reads. It may run queries; only a handler may send a nested command.",
            refusedInside.Message
        );
        Assert.Equal("thing", read.Value);
        Assert.False(mayChangeInInner);
        Assert.StartsWith("A command was sent from the inner step,", refusedInInner.Message);
        Assert.True(mayChangeAfter);
    }
}
