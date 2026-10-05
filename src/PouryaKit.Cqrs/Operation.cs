using System.Collections.Immutable;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The Operation running in the current scope, shared by every command and query dispatched inside it. A scope holds
/// one Operation: its outermost command owns the Unit of Work, so a second outermost command needs a new scope.
/// </summary>
public sealed class Operation
{
    private int _runningCommands;
    private int _runningQueries;
    private bool _commandStarted;
    private bool _unitOfWorkActive;
    private int _runningChecks;

    /// <summary>
    /// True once a command or a Domain Event Handler inside the Operation has failed, whether or not its caller read the
    /// Result. The Unit of Work then rolls back; nothing clears it.
    /// </summary>
    public bool IsRollbackOnly => !RollbackErrors.IsEmpty;

    /// <summary>
    /// The Errors of the first failed command or Domain Event Handler, which the rolled-back Operation fails with; empty
    /// until then.
    /// </summary>
    public ImmutableArray<Error> RollbackErrors { get; private set; } = [];

    /// <summary>
    /// The Module the Operation runs in, as its scope's opener declared it; null in an application without Modules.
    /// It belongs to the scope, not to the <see cref="OperationContext"/>: work an Operation causes may run in another
    /// Module.
    /// </summary>
    public Type? Module { get; private set; }

    internal OperationContext? Context { get; private set; }

    internal void Begin(OperationContext context, Type? module)
    {
        if (Context is not null)
        {
            throw new InvalidOperationException(
                "This scope's Operation has already begun. A scope runs one Operation; open a new scope for another."
            );
        }

        Context = context;
        Module = module;
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

        if (_runningCommands == 0 && _commandStarted)
        {
            throw new InvalidOperationException(
                "A command already ran as the Operation of this scope. Open a new scope for each Operation; a used Unit of Work is never run again."
            );
        }

        // A nested command joins the active Unit of Work; with none active, its handler would run with no owner.
        if (_runningCommands > 0 && !_unitOfWorkActive)
        {
            throw new InvalidOperationException(
                "A command was sent before the Operation's Unit of Work started. Only a handler may send a nested command."
            );
        }

        _commandStarted = true;
        return _runningCommands++ == 0;
    }

    internal void ExitCommand() => _runningCommands--;

    internal void StartUnitOfWork() => _unitOfWorkActive = true;

    internal void EndUnitOfWork() => _unitOfWorkActive = false;

    internal void EnterChecks() => _runningChecks++;

    internal void ExitChecks() => _runningChecks--;

    internal void EnterQuery() => _runningQueries++;

    internal void ExitQuery() => _runningQueries--;
}
