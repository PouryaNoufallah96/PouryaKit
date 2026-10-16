using System.Collections.Concurrent;

namespace M2.Ledger;

// Test hooks keyed by account, process-wide so every Host in the test process sees them: a one-shot gate the first command
// on the account waits at after it read its stream, a hold and a failure for the welcome handler, and a count of the
// welcome handler's runs.
internal static class LedgerProbe
{
    private static readonly ConcurrentDictionary<Guid, Gate> Gates = new();
    private static readonly ConcurrentDictionary<Guid, TimeSpan> Holds = new();
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

    public static void Hold(Guid account, TimeSpan time) => Holds[account] = time;

    public static Task HoldAsync(Guid account, CancellationToken cancellationToken) =>
        Holds.TryGetValue(account, out var time) ? Task.Delay(time, cancellationToken) : Task.CompletedTask;

    public static void Fail(Guid account) => Failures[account] = true;

    public static bool Fails(Guid account) => Failures.ContainsKey(account);

    public static void Ran(Guid account) => Runs.AddOrUpdate(account, 1, (_, runs) => runs + 1);

    public static int RunsOf(Guid account) => Runs.GetValueOrDefault(account);
}

internal sealed class Gate
{
    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
