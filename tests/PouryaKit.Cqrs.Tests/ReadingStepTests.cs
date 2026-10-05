using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs.Tests.Module;

namespace PouryaKit.Cqrs.Tests;

public sealed class ReadingStepTests
{
    [Fact]
    public async Task A_step_that_only_reads_refuses_commands_and_runs_queries_until_it_and_every_step_around_it_close()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();
        var operation = scope.ServiceProvider.GetRequiredService<Operation>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var outer = operation.OnlyReading("the outer step");
        var inner = operation.OnlyReading("the inner step");
        var refusedInside = await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.Ping(dispatcher));
        var read = await ThingsGateway.GetThing(dispatcher);
        inner.Dispose();
        var mayChangeInOuter = operation.MayChangeState;
        var refusedOutside = await Assert.ThrowsAsync<InvalidOperationException>(() => ThingsGateway.Ping(dispatcher));
        outer.Dispose();
        outer.Dispose();
        var mayChangeAfter = operation.MayChangeState;

        Assert.Equal(
            "A command was sent from the inner step, a step that only reads. It may run queries; only a handler may send a nested command.",
            refusedInside.Message
        );
        Assert.Equal("thing", read.Value);
        Assert.False(mayChangeInOuter);
        Assert.StartsWith("A command was sent from the outer step,", refusedOutside.Message);
        Assert.True(mayChangeAfter);
    }
}
