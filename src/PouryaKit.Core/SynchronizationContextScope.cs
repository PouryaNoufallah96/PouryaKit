namespace PouryaKit.Core;

/// <summary>
/// Replaces the current thread's <see cref="SynchronizationContext"/> until disposed, then restores the one it found,
/// whether the scope ends normally or by an exception: <c>using var scope = SynchronizationContextScope.Enter();</c>.
/// </summary>
/// <remarks>
/// Input contract: enter and dispose the scope on the same thread, once, in a <c>using</c> statement or declaration.
/// The context belongs to the thread, so the scope is a <see langword="ref"/> struct, which the compiler keeps from
/// living across an <see langword="await"/>, which could resume on another thread. A <see langword="default"/> scope
/// was never entered, and disposing it changes nothing.
/// </remarks>
public readonly ref struct SynchronizationContextScope : IDisposable
{
    private readonly SynchronizationContext? _previous;
    private readonly bool _entered;

    private SynchronizationContextScope(SynchronizationContext? previous)
    {
        _previous = previous;
        _entered = true;
    }

    /// <summary>
    /// Sets <see cref="SynchronizationContext.Current"/> to <paramref name="context"/>, or to none when it is
    /// <see langword="null"/>, so that what the scope's code awaits does not resume on the replaced context.
    /// </summary>
    public static SynchronizationContextScope Enter(SynchronizationContext? context = null)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        return new SynchronizationContextScope(previous);
    }

    /// <summary>Restores the context that was current when the scope was entered.</summary>
    public void Dispose()
    {
        if (_entered)
        {
            SynchronizationContext.SetSynchronizationContext(_previous);
        }
    }
}
