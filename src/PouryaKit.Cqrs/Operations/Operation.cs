using System.Collections.Immutable;
using System.Diagnostics;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The Operation running in the current scope, shared by every command and query dispatched inside it. A scope holds
/// one Operation: its outermost command owns the Unit of Work, so a second outermost command needs a new scope.
/// </summary>
public sealed class Operation
{
    private readonly Func<IReadingStateGuard?> _readingState;
    private static readonly Error ReadingChangedState = Error.Unexpected(
        "Operation.ReadingChangedState",
        "Code that only reads changed tracked state, so the Operation was rolled back."
    );

    /// <summary>
    /// Creates an Operation without a persistence state guard. The Kit's service registration attaches the current
    /// Unit of Work's guard when one exists; a manually constructed Operation guards only the Kit's write entry points.
    /// </summary>
    public Operation() : this(() => null) { }

    internal Operation(Func<IReadingStateGuard?> readingState) => _readingState = readingState;

    private int _runningCommands;
    private int _runningQueries;
    private bool _commandStarted;
    private HashSet<string>? _tagsToEvict;
    private int _runningChecks;
    // The open steps that only read, in the order opened.
    private readonly List<ReadingStep> _readingSteps = [];

    /// <summary>
    /// True once a command or a Domain Event Handler inside the Operation has failed, or code that only reads changed
    /// tracked state, whether or not its caller handled the failure. The Unit of Work then rolls back; nothing clears it.
    /// </summary>
    public bool IsRollbackOnly => !RollbackErrors.IsEmpty;

    /// <summary>
    /// The Errors of the first failed command, Domain Event Handler or read-state check, which the rolled-back Operation
    /// fails with; empty until then.
    /// </summary>
    public ImmutableArray<Error> RollbackErrors { get; private set; } = [];

    /// <summary>
    /// The Module the Operation runs in, as its scope's opener declared it; null in an application without Modules.
    /// It belongs to the scope, not to the <see cref="OperationContext"/>: work an Operation causes may run in another
    /// Module.
    /// </summary>
    public Type? Module { get; private set; }

    /// <summary>
    /// False while a query, an authorizer or a validator runs in the Operation, at any depth, including those of a command
    /// nested in another, and while a step opened with <see cref="OnlyReading(string)"/> runs; true otherwise, as in a command's
    /// handler or a Domain Event Handler. Where it is false the Operation only reads: a command is refused there, and so
    /// is any other guarded write. A persistence guard also checks for changes to tracked objects at the end of the
    /// read, whether it succeeded, failed or threw, and marks a change rollback-only.
    /// </summary>
    public bool MayChangeState => _runningQueries == 0 && _runningChecks == 0 && _readingSteps.Count == 0;

    /// <summary>
    /// Opens a step that only reads, named <paramref name="step"/>, until the returned scope is disposed. A Kit package
    /// opens it around application code it runs after the Operation's last change, such as the code that builds what
    /// the Unit of Work publishes. While it is open <see cref="MayChangeState"/> is false, so every write the Kit
    /// guards is refused, and a command is refused with a message naming <paramref name="step"/>; a query runs as usual.
    /// Steps nest; disposing one closes that step only, whatever else is open, and disposing it again does nothing.
    /// Feature code never opens one.
    /// </summary>
    public IDisposable OnlyReading(string step) => OnlyReading(step, compareTrackedState: true);

    /// <summary>
    /// Opens a step that only reads. The Kit may set <paramref name="compareTrackedState"/> to false only when its
    /// caller owns a comparison over the whole batch, as the Snapshot step does. Write entry points stay guarded;
    /// normal queries and checks still compare their own tracked state. Feature code never opens one.
    /// </summary>
    public IDisposable OnlyReading(string step, bool compareTrackedState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        var reading = new ReadingStep(this, step, compareTrackedState ? _readingState()?.OnlyReading(step) : null);
        _readingSteps.Add(reading);
        return reading;
    }

    /// <summary>The Operation Context the scope's opener began the Operation with; null until it begins.</summary>
    public OperationContext? Context { get; private set; }

    /// <summary>
    /// The trace context of the work that caused this Operation, as a message delivery restored it from the message it
    /// runs; default when the scope's opener restored none. The Operation's activity starts with it as its parent when
    /// no activity of that trace is current (ADR 0009).
    /// </summary>
    public ActivityContext TraceParent { get; private set; }

    internal void Begin(OperationContext context, Type? module, ActivityContext traceParent)
    {
        if (Context is not null)
        {
            throw new InvalidOperationException(
                "This scope's Operation has already begun. A scope runs one Operation; open a new scope for another."
            );
        }

        Context = context;
        Module = module;
        TraceParent = traceParent;
    }

    internal void MarkRollbackOnly(ImmutableArray<Error> errors)
    {
        if (!IsRollbackOnly)
        {
            RollbackErrors = errors;
        }
    }

    /// <summary>Records a command starting and returns true when it is the outermost one, which owns the Unit of Work.</summary>
    internal bool EnterCommand()
    {
        // Authorizers and validators run before their command's handler, at every depth; a command they sent would
        // change state for a command that may still be refused.
        if (_runningChecks > 0)
        {
            throw new InvalidOperationException(
                "A command was sent from an authorizer or a validator. They may run queries; only a handler may send a nested command."
            );
        }

        if (_runningQueries > 0)
        {
            throw new InvalidOperationException(
                "A query handler sent a command. A query runs without a Unit of Work, so it cannot change state."
            );
        }

        if (_readingSteps.Count > 0)
        {
            throw new InvalidOperationException(
                $"A command was sent from {_readingSteps[^1].Name}, a step that only reads. It may run queries; only a handler may send a nested command."
            );
        }

        if (_runningCommands == 0 && _commandStarted)
        {
            throw new InvalidOperationException(
                "A command already ran as the Operation of this scope. Open a new scope for each Operation; a used Unit of Work is never run again."
            );
        }

        // A nested command joins the active Unit of Work; with none active, its handler would run with no owner.
        if (_runningCommands > 0 && !IsUnitOfWorkActive)
        {
            throw new InvalidOperationException(
                "A command was sent before the Operation's Unit of Work started. Only a handler may send a nested command."
            );
        }

        _commandStarted = true;
        return _runningCommands++ == 0;
    }

    internal void ExitCommand() => _runningCommands--;

    internal void StartUnitOfWork() => IsUnitOfWorkActive = true;

    internal void EndUnitOfWork() => IsUnitOfWorkActive = false;

    /// <summary>
    /// True while the Unit of Work of the Operation's outermost command runs: from the moment the dispatcher starts it,
    /// once that command's authorizers and validator have passed, until it has committed or rolled back. The command's
    /// handler, every command nested in it, its Domain Event Handlers and a delivery's Notification Handler or
    /// Integration Event Handler, which the Kit runs inside a command of its own, all run while it is true, and a query
    /// there may see uncommitted state. It is false before the Operation begins, in a begun scope that sends no command,
    /// in a query sent on its own, in the outermost command's authorizers and validator, and once the Unit of Work has
    /// ended. A Kit package reads it to refuse a write that no Unit of Work would commit; Feature code has no need to.
    /// </summary>
    public bool IsUnitOfWorkActive { get; private set; }

    /// <summary>
    /// True while the running query is the Operation's outermost step: no command, authorizer, validator, other query
    /// or step that only reads is open around it, so none of them can have changed the tracked state it reads. The
    /// query counts itself, since it is entered before its steps run.
    /// </summary>
    internal bool IsOutermostQuery =>
        _runningQueries == 1 && _runningCommands == 0 && _runningChecks == 0 && _readingSteps.Count == 0;

    /// <summary>The cache tags the Operation's commands declared, outermost and nested, evicted only once it commits.</summary>
    internal IReadOnlyCollection<string> TagsToEvict => (IReadOnlyCollection<string>?)_tagsToEvict ?? [];

    internal void EvictAfterCommit(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            (_tagsToEvict ??= []).Add(tag);
        }
    }

    internal IDisposable EnterChecks()
    {
        var reading = new ReadingBoundary(this, _readingState()?.OnlyReading("An authorizer or a validator"), () => _runningChecks--);
        _runningChecks++;
        return reading;
    }

    internal IDisposable EnterQuery()
    {
        var reading = new ReadingBoundary(this, _readingState()?.OnlyReading("A query"), () => _runningQueries--);
        _runningQueries++;
        return reading;
    }

    private sealed class ReadingBoundary(Operation operation, IDisposable? guard, Action close) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                guard?.Dispose();
            }
            catch
            {
                operation.MarkRollbackOnly([ReadingChangedState]);
                throw;
            }
            finally
            {
                close();
            }
        }
    }

    // Closes its own step, once; any other step stays as it is.
    private sealed class ReadingStep(Operation operation, string name, IDisposable? guard) : IDisposable
    {
        private ReadingBoundary? _boundary;

        public string Name { get; } = name;

        public void Dispose() => (_boundary ??= new ReadingBoundary(operation, guard, () => operation._readingSteps.Remove(this))).Dispose();
    }
}
