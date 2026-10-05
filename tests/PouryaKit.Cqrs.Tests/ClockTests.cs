using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs.Tests;

public sealed class ClockTests
{
    [Fact]
    public async Task A_domain_event_raised_in_an_operation_takes_its_hosts_clock_and_parallel_operations_keep_their_own()
    {
        var monday = new DateTimeOffset(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);
        var tuesday = monday.AddDays(1);
        var bothRunning = new Rendezvous(2);
        await using var mondayHost = Host(monday, bothRunning);
        await using var tuesdayHost = Host(tuesday, bothRunning);

        var raised = await Task.WhenAll(Stamp(mondayHost), Stamp(tuesdayHost));

        Assert.Equal([monday, tuesday], raised.Select(domainEvent => domainEvent.OccurredOn));
        Assert.Same(TimeProvider.System, DomainClock.Current);
    }

    private static async Task<StampedDomainEvent> Stamp(ServiceProvider host)
    {
        await using var scope = host.CreateAsyncScope();
        var stamped = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new StampCommand(), TestContext.Current.CancellationToken);
        return stamped.Value;
    }

    // Registered by hand: this test assembly holds deliberately invalid operations for the registration tests.
    private static ServiceProvider Host(DateTimeOffset now, Rendezvous bothRunning) =>
        new ServiceCollection()
            .AddSingleton<TimeProvider>(new FixedTime(now))
            .AddPouryaKitCqrs()
            .AddSingleton(bothRunning)
            .AddScoped<CommandHandler<StampCommand, StampedDomainEvent>, StampCommandHandler>()
            .AddScoped<IUnitOfWork, PassThroughUnitOfWork>()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
}

internal sealed record StampedDomainEvent : DomainEvent;

internal sealed record StampCommand : Command<StampedDomainEvent>;

// Raises its event only once both Operations are running, so their two clock bindings are live at the same time.
internal sealed class StampCommandHandler(Rendezvous bothRunning) : CommandHandler<StampCommand, StampedDomainEvent>
{
    public override async Task<Result<StampedDomainEvent>> Handle(StampCommand command, CancellationToken cancellationToken)
    {
        await bothRunning.ArriveAsync();
        return new StampedDomainEvent();
    }
}

internal sealed class Rendezvous(int parties)
{
    private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    public Task ArriveAsync()
    {
        if (Interlocked.Increment(ref _arrived) == parties)
        {
            _all.SetResult();
        }

        return _all.Task;
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class PassThroughUnitOfWork : IUnitOfWork
{
    public Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken) =>
        command(cancellationToken);
}
