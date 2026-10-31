using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;
using PouryaKit.Observability;

namespace PouryaKit.Cqrs.Tests;

public sealed class ObservedDispatcherTests
{
    // Nothing in this test process listens to the Kit's source or meter. The wrapped dispatcher answers with tasks that
    // are already complete, so whatever is allocated, the wrapper allocated: warmed, it hands back the inner task itself.
    [Fact]
    public void With_nothing_listening_the_observed_dispatcher_returns_the_dispatchers_own_task_and_allocates_nothing()
    {
        using var host = new ServiceCollection()
            .AddMetrics()
            .AddScoped<IDispatcher, CompletedDispatcher>()
            .AddPouryaKitCqrs()
            .AddPouryaKitObservability()
            .BuildServiceProvider();
        using var scope = host.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var cancellationToken = TestContext.Current.CancellationToken;
        Done done = new();
        Answer answer = new();
        Ask ask = new();
        Dispatch(1_000);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Dispatch(10_000);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(
            (true, true, true, 0L),
            (
                ReferenceEquals(
                    CompletedDispatcher.Succeeded,
                    dispatcher.SendAsync(done, cancellationToken)
                ),
                ReferenceEquals(
                    CompletedDispatcher.Answered,
                    dispatcher.SendAsync(answer, cancellationToken)
                ),
                ReferenceEquals(
                    CompletedDispatcher.Answered,
                    dispatcher.QueryAsync(ask, cancellationToken)
                ),
                allocated
            )
        );

        void Dispatch(int times)
        {
            for (var i = 0; i < times; i++)
            {
                dispatcher.SendAsync(done, cancellationToken).GetAwaiter().GetResult();
                dispatcher.SendAsync(answer, cancellationToken).GetAwaiter().GetResult();
                dispatcher.QueryAsync(ask, cancellationToken).GetAwaiter().GetResult();
            }
        }
    }

    private sealed record Done : Command;

    private sealed record Answer : Command<string>;

    private sealed record Ask : Query<string>;

    private sealed class CompletedDispatcher : IDispatcher
    {
        public static readonly Task<Result> Succeeded = Task.FromResult(Result.Success());

        public static readonly Task<Result<string>> Answered = Task.FromResult(
            Result.Success("answer")
        );

        public Task<Result> SendAsync(
            Command command,
            CancellationToken cancellationToken = default
        ) => Succeeded;

        public Task<Result<TResponse>> SendAsync<TResponse>(
            Command<TResponse> command,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull => (Task<Result<TResponse>>)(object)Answered;

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            Query<TResponse> query,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull => (Task<Result<TResponse>>)(object)Answered;
    }
}
