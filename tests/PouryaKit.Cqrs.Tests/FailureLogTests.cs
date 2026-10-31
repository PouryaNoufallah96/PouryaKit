using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs.Tests;

public sealed class FailureLogTests
{
    private const int CodeLength = 20_000;

    // The failure log's text of error codes: two codes and the separator.
    private const long JoinedCodeBytes = (2 * CodeLength + 2) * sizeof(char);

    // Every step completes at once, so the dispatch allocates on this thread only, and the logger writes nothing, so
    // what an entry costs is what the dispatcher builds for it. Two long codes make their joined text larger than all
    // else a dispatch allocates.
    [Fact]
    public async Task A_failed_operation_joins_its_error_codes_only_when_its_log_entry_is_written()
    {
        var entries = new EntrySwitch();
        await using var host = new ServiceCollection()
            .AddPouryaKitCqrs()
            .AddSingleton(entries)
            .AddSingleton(typeof(ILogger<>), typeof(SwitchedLogger<>))
            .AddScoped<QueryHandler<RefusedQuery, string>, RefusedQueryHandler>()
            .BuildServiceProvider();
        await Dispatch();
        await Dispatch();

        entries.On = false;
        var off = await Dispatch();
        entries.On = true;
        var on = await Dispatch();

        Assert.True(
            off < JoinedCodeBytes,
            $"With its entry off, a failed query allocated {off:N0} bytes."
        );
        Assert.True(
            on >= JoinedCodeBytes,
            $"With its entry on, a failed query allocated {on:N0} bytes."
        );

        async Task<long> Dispatch()
        {
            await using var scope = host.CreateAsyncScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var refused = dispatcher.QueryAsync(
                new RefusedQuery(),
                TestContext.Current.CancellationToken
            );
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(refused.IsCompletedSuccessfully);
            Assert.True((await refused).IsFailure);
            return allocated;
        }
    }

    private sealed record RefusedQuery : Query<string>;

    private sealed class RefusedQueryHandler : QueryHandler<RefusedQuery, string>
    {
        private static readonly ImmutableArray<Error> Reasons =
        [
            Error.Validation(new string('A', CodeLength), "The first reason."),
            Error.Validation(new string('B', CodeLength), "The second reason."),
        ];

        private static readonly Task<Result<string>> Refused = Task.FromResult<Result<string>>(
            Reasons
        );

        public override Task<Result<string>> Handle(
            RefusedQuery query,
            CancellationToken cancellationToken
        ) => Refused;
    }

    private sealed class EntrySwitch
    {
        public bool On { get; set; } = true;
    }

    private sealed class SwitchedLogger<T>(EntrySwitch entries) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => entries.On;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) { }
    }
}
