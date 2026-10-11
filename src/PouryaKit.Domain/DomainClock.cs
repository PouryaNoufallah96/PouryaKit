namespace PouryaKit.Domain;

/// <summary>
/// The clock a Domain Event reads its time from: the one bound to the running Operation, or the system clock where
/// none is bound. The dispatcher binds the Host's <see cref="TimeProvider"/> for each Operation, so Feature code never
/// binds it. A business rule that compares with "now" takes the time as a parameter instead of reading this.
/// </summary>
public static class DomainClock
{
    private static readonly AsyncLocal<TimeProvider?> Bound = new();

    /// <summary>
    /// The clock bound to the current asynchronous flow, or <see cref="TimeProvider.System"/> where none is bound.
    /// </summary>
    public static TimeProvider Current => Bound.Value ?? TimeProvider.System;

    /// <summary>
    /// Binds <paramref name="timeProvider"/> for the current asynchronous flow until the returned scope is disposed,
    /// which restores the clock bound before. Flows that run in parallel each see only their own binding.
    /// </summary>
    public static IDisposable Use(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var previous = Bound.Value;
        Bound.Value = timeProvider;
        return new Binding(previous);
    }

    private sealed class Binding(TimeProvider? previous) : IDisposable
    {
        public void Dispose() => Bound.Value = previous;
    }
}
