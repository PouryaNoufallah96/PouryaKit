namespace PouryaKit.Cqrs;

// The context of the Operation whose authorized work the current code runs in: set by the dispatcher once a command's or
// a query's checks passed, cleared while the checks of one nested in it run, and ended when it returns. Only Continue()
// on that exact context instance is a trusted continuation (G.6, ADR 0007). It verifies that claim and never supplies a
// context, which stays explicit. A task started inside the work that outlives it sees it end.
internal sealed class AuthorizedOperation : IDisposable
{
    private static readonly AsyncLocal<AuthorizedOperation?> Current = new();

    private readonly OperationContext? _context;
    private readonly AuthorizedOperation? _previous;

    // Read by a task started inside the work, on another thread than the one that ends it.
    private volatile bool _ended;

    private AuthorizedOperation(OperationContext? context)
    {
        _context = context;
        _previous = Current.Value;
        Current.Value = this;
    }

    public static bool Runs(OperationContext context) =>
        Current.Value is { _ended: false } running && ReferenceEquals(running._context, context);

    /// <summary>The checks of a command or query, at any depth: what they run is not yet authorized.</summary>
    public static AuthorizedOperation Checking() => new(null);

    /// <summary>The work of a command or query whose checks passed, running with <paramref name="context"/>.</summary>
    public static AuthorizedOperation Running(OperationContext? context) => new(context);

    public void Dispose()
    {
        _ended = true;
        Current.Value = _previous;
    }
}
