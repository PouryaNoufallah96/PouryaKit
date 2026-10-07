using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The retention of idempotency keys against PostgreSQL. Each test starts a Host whose TimeProvider is the test clock, so
// the store stamps a key with the test's time and the cleanup ticks when the test advances it. CountNoteCommand answers
// a number no other run of its text answers, so a replay tells itself apart from a new run.
public sealed class IdempotencyRetentionTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly string _text = $"note {Guid.CreateVersion7()}";

    [Fact]
    public async Task A_key_replays_for_its_whole_retention_and_once_a_cleanup_removed_it_its_request_runs_as_a_new_one()
    {
        using var clock = new TestClock(Monday);
        var host = await StartHostAsync(clock, new RecordedLogs());

        Assert.Equal(1, await CountAsync(host, "old"));
        clock.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(2, await CountAsync(host, "young"));
        // With the default retention of 24 hours, the next cleanup removes the keys reserved before Monday 09:59.
        clock.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1));
        await Fixture.WaitUntilAsync(
            "not exists (select 1 from stub.pouryakit_idempotency_keys where request_id = 'old')",
            "No cleanup removed the key reserved before its retention."
        );
        var young = await CountAsync(host, "young");
        var old = await CountAsync(host, "old");
        await StopAsync(host);

        Assert.Equal(2, young);
        Assert.Equal(3, old);
        Assert.Equal(
            [["old", Monday.AddHours(25).AddMinutes(-1).UtcDateTime], ["young", Monday.AddHours(1).UtcDateTime]],
            await Fixture.RowsAsync("select request_id, reserved_at from stub.pouryakit_idempotency_keys order by request_id")
        );
    }

    [Fact]
    public async Task A_cleanup_removes_more_expired_keys_than_one_batch_in_batches_of_the_configured_size()
    {
        using var clock = new TestClock(Monday);
        var logs = new RecordedLogs();
        var host = await StartHostAsync(clock, logs, options => options.BatchSize = 2);
        foreach (var requestId in new[] { "r1", "r2", "r3", "r4", "r5" })
        {
            await CountAsync(host, requestId);
        }

        clock.Time.Advance(TimeSpan.FromHours(25));
        var removed = await WaitForLogAsync(logs, LogLevel.Information, "Removed ");
        await StopAsync(host);

        Assert.Equal(
            $"Removed 5 idempotency keys reserved before {Monday.AddHours(1):O} from \"stub\".pouryakit_idempotency_keys in 3 batches",
            removed
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_idempotency_keys"));
    }

    [Fact]
    public async Task A_failed_cleanup_is_logged_and_the_next_one_removes_the_expired_keys()
    {
        using var clock = new TestClock(Monday);
        var logs = new RecordedLogs();
        var host = await StartHostAsync(clock, logs);
        await CountAsync(host, "r1");
        // The column the cleanup reads is gone, so its statement fails in PostgreSQL.
        await Fixture.RowsAsync("alter table stub.pouryakit_idempotency_keys rename column reserved_at to reserved");
        try
        {
            clock.Time.Advance(TimeSpan.FromHours(25));
            await WaitForLogAsync(logs, LogLevel.Error, "Removing the expired idempotency keys of StubDbContext failed");
        }
        finally
        {
            await Fixture.RowsAsync("alter table stub.pouryakit_idempotency_keys rename column reserved to reserved_at");
        }

        clock.Time.Advance(TimeSpan.FromHours(1));
        await Fixture.WaitUntilAsync(
            "not exists (select 1 from stub.pouryakit_idempotency_keys)",
            "The cleanup after a failed one removed nothing."
        );
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;
        await StopAsync(host);

        Assert.False(stopping);
    }

    [Fact]
    public async Task A_host_whose_retention_is_infinite_keeps_every_key()
    {
        using var clock = new TestClock(Monday);
        var host = await StartHostAsync(clock, new RecordedLogs(), options => options.Retention = Timeout.InfiniteTimeSpan);
        Assert.Equal(1, await CountAsync(host, "r1"));

        clock.Time.Advance(TimeSpan.FromDays(30));
        // A cleanup that ran would have removed the key within milliseconds of the advance.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var repeat = await CountAsync(host, "r1");
        await StopAsync(host);

        Assert.Equal(1, repeat);
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from stub.pouryakit_idempotency_keys"));
    }

    // A repeat's insert finds the key held and locks nothing, so the cleanup can remove the expired key before the
    // store reads the recorded success. The interceptor runs that cleanup at exactly that point.
    [Fact]
    public async Task A_repeat_whose_expired_key_a_cleanup_removes_between_its_reservation_and_its_read_runs_as_a_new_request()
    {
        using var clock = new TestClock(Monday);
        var cleanedBetween = new AfterHeldKey(async () =>
        {
            clock.Time.Advance(TimeSpan.FromHours(25));
            await Fixture.WaitUntilAsync(
                "not exists (select 1 from stub.pouryakit_idempotency_keys)",
                "No cleanup removed the held key before the repeat read it."
            );
        });
        var host = await StartHostAsync(
            clock,
            new RecordedLogs(),
            services: services => services.ConfigureDbContext<StubDbContext>(options => options.AddInterceptors(cleanedBetween))
        );
        Assert.Equal(1, await CountAsync(host, "r1"));

        var repeat = await CountAsync(host, "r1");
        await StopAsync(host);

        Assert.True(cleanedBetween.Ran);
        Assert.Equal(2, repeat);
        Assert.Equal(
            [["r1", Monday.AddHours(25).UtcDateTime]],
            await Fixture.RowsAsync("select request_id, reserved_at from stub.pouryakit_idempotency_keys")
        );
    }

    // A database migrated before CreateIdempotencyTable added reserved_at, during Wave 2, has a table without it.
    [Fact]
    public async Task A_key_table_without_reserved_at_fails_the_operation_naming_the_column_and_its_fix()
    {
        await Fixture.RowsAsync("alter table stub.pouryakit_idempotency_keys rename column reserved_at to reserved");
        InvalidOperationException thrown;
        try
        {
            thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => CountAsync(Fixture.Host, "r1"));
        }
        finally
        {
            await Fixture.RowsAsync("alter table stub.pouryakit_idempotency_keys rename column reserved to reserved_at");
        }

        Assert.StartsWith(
            "\"stub\".pouryakit_idempotency_keys of StubDbContext has no reserved_at column: it was created before CreateIdempotencyTable added it.",
            thrown.Message,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "alter table \"stub\".pouryakit_idempotency_keys add column reserved_at timestamp with time zone not null",
            thrown.Message,
            StringComparison.Ordinal
        );
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes"));
    }

    public static TheoryData<TimeSpan, TimeSpan, int, string[]> OutOfRange() =>
        new()
        {
            {
                TimeSpan.Zero,
                TimeSpan.FromHours(-1),
                0,
                [
                    "The idempotency key Retention is 00:00:00; it must be positive and at most 36500.00:00:00, or Timeout.InfiniteTimeSpan to keep every key.",
                    "The idempotency key CleanupInterval is -01:00:00; it must be from 00:00:00.0010000 to 49.17:02:47.2940000.",
                    "The idempotency key BatchSize is 0; it must be at least 1.",
                ]
            },
            {
                TimeSpan.FromDays(36501),
                TimeSpan.FromMilliseconds(0.1),
                1000,
                [
                    "The idempotency key Retention is 36501.00:00:00; it must be positive and at most 36500.00:00:00, or Timeout.InfiniteTimeSpan to keep every key.",
                    "The idempotency key CleanupInterval is 00:00:00.0001000; it must be from 00:00:00.0010000 to 49.17:02:47.2940000.",
                ]
            },
            {
                TimeSpan.FromHours(24),
                TimeSpan.FromDays(50),
                1000,
                ["The idempotency key CleanupInterval is 50.00:00:00; it must be from 00:00:00.0010000 to 49.17:02:47.2940000."]
            },
        };

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public async Task A_host_whose_retention_interval_or_batch_size_is_out_of_range_fails_to_start_naming_each(
        TimeSpan retention,
        TimeSpan cleanupInterval,
        int batchSize,
        string[] failures
    )
    {
        using var clock = new TestClock(Monday);
        await using var host = Compose(
            clock,
            new RecordedLogs(),
            options =>
            {
                options.Retention = retention;
                options.CleanupInterval = cleanupInterval;
                options.BatchSize = batchSize;
            }
        );

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(failures, failure.Failures);
    }

    private WebApplication Compose(
        TestClock clock,
        RecordedLogs logs,
        Action<PouryaKitIdempotencyOptions>? configure = null,
        Action<IServiceCollection>? services = null
    ) =>
        AppFixture.ComposeHost(
            "Development",
            Fixture.ConnectionString,
            logs,
            builder =>
            {
                builder.Services.AddSingleton<TimeProvider>(clock.Time);
                builder.Services.Configure(configure ?? (_ => { }));
                services?.Invoke(builder.Services);
            }
        );

    private async Task<WebApplication> StartHostAsync(
        TestClock clock,
        RecordedLogs logs,
        Action<PouryaKitIdempotencyOptions>? configure = null,
        Action<IServiceCollection>? services = null
    )
    {
        var host = Compose(clock, logs, configure, services);
        await HostFixture.StartHostAsync(host, TestContext.Current.CancellationToken);
        return host;
    }

    // The Notifications the Host's runs stored are delivered before it stops, by whichever Host takes them.
    private async Task StopAsync(WebApplication host)
    {
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await host.DisposeAsync();
    }

    private async Task<int> CountAsync(IHost host, string requestId) =>
        (
            await host
                .Services.GetRequiredService<IGatewayProcessor<StubModule>>()
                .SendAsync(
                    new CountNoteCommand(_text),
                    new OperationContext(Actor.Person("ada")) { RequestId = requestId },
                    TestContext.Current.CancellationToken
                )
        ).Value.Number;

    // Runs its action once, right after an insert of a Stub key that found the key held, before the store reads it.
    private sealed class AfterHeldKey(Func<Task> action) : DbCommandInterceptor
    {
        private int _ran;

        public bool Ran => _ran == 1;

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default
        )
        {
            if (
                result == 0
                && command.CommandText.StartsWith("insert into \"stub\".pouryakit_idempotency_keys", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _ran, 1) == 0
            )
            {
                await action();
            }

            return result;
        }
    }

    private static async Task<string> WaitForLogAsync(RecordedLogs logs, LogLevel level, string prefix)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (true)
        {
            var entry = logs.Entries.FirstOrDefault(entry =>
                entry.Category == "PouryaKit.Persistence.EFCore.IdempotencyKeyCleanup"
                && entry.Level == level
                && entry.Message.StartsWith(prefix, StringComparison.Ordinal)
            );
            if (entry.Message is not null)
            {
                return entry.Message;
            }

            Assert.False(timeout.IsCancellationRequested, $"The cleanup never logged \"{prefix}\".");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }
}
