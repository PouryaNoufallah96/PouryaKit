namespace PouryaKit.Cqrs;

/// <summary>
/// A persistence package's Kit seam for checking the tracked state across a step that only reads. The returned scope
/// compares on disposal, including when the step failed or threw, and throws when the state changed. Feature code
/// never uses it; the Operation marks a refused change rollback-only before the exception reaches its caller.
/// </summary>
public interface IReadingStateGuard
{
    /// <summary>
    /// Captures the tracked state before a step that only reads, which <paramref name="step"/> names in a refusal.
    /// </summary>
    IDisposable OnlyReading(string step);
}
