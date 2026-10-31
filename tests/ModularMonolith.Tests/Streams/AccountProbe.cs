using System.Collections.Concurrent;

namespace ModularMonolith.Tests.Streams;

// Test hooks keyed by account, process-wide so every Host in the test process sees them: a one-shot gate the first command
// on the account waits at after it read its stream, a hold and a failure for the welcome, and the runs of each handler.
internal static class AccountProbe
{
    private static readonly ConcurrentDictionary<Guid, Gate> Gates = new();
    private static readonly ConcurrentDictionary<
        Guid,
        (TimeSpan Time, TaskCompletionSource Reached)
    > Holds = new();
    private static readonly ConcurrentDictionary<Guid, bool> Failures = new();
    private static readonly ConcurrentDictionary<Guid, int> Runs = new();

    public static Gate Close(Guid account) => Gates[account] = new Gate();

    public static Task PassAsync(Guid account)
    {
        if (!Gates.TryRemove(account, out var gate))
        {
            return Task.CompletedTask;
        }

        gate.Reached.TrySetResult();
        return gate.Opened.Task;
    }

    // Completes when the welcome reaches the hold, with its Receipt recorded and its transaction open.
    public static Task Hold(Guid account, TimeSpan time)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Holds[account] = (time, reached);
        return reached.Task;
    }

    public static Task HoldAsync(Guid account, CancellationToken cancellationToken)
    {
        if (!Holds.TryGetValue(account, out var hold))
        {
            return Task.CompletedTask;
        }

        hold.Reached.TrySetResult();
        return Task.Delay(hold.Time, cancellationToken);
    }

    public static void Fail(Guid account) => Failures[account] = true;

    public static bool Fails(Guid account) => Failures.ContainsKey(account);

    public static void Ran(Guid key) => Runs.AddOrUpdate(key, 1, (_, runs) => runs + 1);

    public static int RunsOf(Guid key) => Runs.GetValueOrDefault(key);
}

internal sealed class Gate
{
    public TaskCompletionSource Reached { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Opened { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
