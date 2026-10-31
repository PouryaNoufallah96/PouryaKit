using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs.Tests.Module;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs.Tests;

public sealed class ReadingStepTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_query_read_violation_is_logged_once_before_any_result_is_logged(bool nested)
    {
        await using var host = TestHost.Build(unitOfWork: typeof(ReadingViolationUnitOfWork));
        using var scope = host.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            nested
                ? ThingsGateway.RunOuterIgnoringAFailedQuery(dispatcher)
                : ThingsGateway.GetThing(dispatcher)
        );

        var trace = scope.ServiceProvider.GetRequiredService<OperationTrace>().Steps;
        var query = nested ? "FindMissingThingQuery" : "GetThingQuery";
        Assert.Equal(
            [$"log:Threw {query} InvalidOperationException"],
            trace.Where(step => step.StartsWith("log:Threw", StringComparison.Ordinal))
        );
        Assert.DoesNotContain($"log:Succeeded {query}", trace);
        Assert.DoesNotContain($"log:Failed {query}", trace);
        Assert.True(scope.ServiceProvider.GetRequiredService<Operation>().IsRollbackOnly);
    }

    [Fact]
    public async Task A_standalone_query_needs_no_persistence_registration()
    {
        var services = new ServiceCollection().AddPouryaKitCqrs(typeof(ThingsGateway).Assembly);
        services.AddScoped<OperationTrace>();
        services.AddScoped(typeof(ILogger<>), typeof(TraceLogger<>));
        await using var host = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
        using var scope = host.CreateScope();

        var result = await ThingsGateway.GetThing(
            scope.ServiceProvider.GetRequiredService<IDispatcher>()
        );

        Assert.Equal("thing", result.Value);
        Assert.False(scope.ServiceProvider.GetRequiredService<Operation>().IsRollbackOnly);
    }

    [Fact]
    public async Task A_step_that_only_reads_refuses_commands_and_runs_queries_until_it_closes_and_closes_only_itself()
    {
        await using var host = TestHost.Build();
        using var scope = host.CreateScope();
        var operation = scope.ServiceProvider.GetRequiredService<Operation>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var outer = operation.OnlyReading("the outer step");
        var inner = operation.OnlyReading("the inner step");
        var refusedInside = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThingsGateway.Ping(dispatcher)
        );
        var read = await ThingsGateway.GetThing(dispatcher);
        // Closed out of order: the inner step stays open.
        outer.Dispose();
        outer.Dispose();
        var mayChangeInInner = operation.MayChangeState;
        var refusedInInner = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThingsGateway.Ping(dispatcher)
        );
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

    private sealed class ReadingViolationUnitOfWork : IUnitOfWork, IReadingStateGuard
    {
        public Task<Result> RunAsync(
            Func<CancellationToken, Task<Result>> command,
            CancellationToken cancellationToken
        ) => command(cancellationToken);

        public IDisposable OnlyReading(string step) => new ReadingViolation(step);

        private sealed class ReadingViolation(string step) : IDisposable
        {
            public void Dispose()
            {
                if (step == "A query")
                {
                    throw new InvalidOperationException("The query changed tracked state.");
                }
            }
        }
    }
}
