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

    /// <summary>
    /// True once a command inside the Operation has failed, whether or not its caller read the Result. The Unit of
    /// Work then rolls back; nothing clears it.
    /// </summary>
    public bool IsRollbackOnly => !RollbackErrors.IsEmpty;

    /// <summary>The Errors of the first failed command, which the rolled-back Operation fails with; empty until then.</summary>
    public ImmutableArray<Error> RollbackErrors { get; private set; } = [];

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

        // Between the outermost command's start and its Unit of Work's start (its authorizers and validators), a
        // nested command would run its handler with no Unit of Work to own it.
        if (_runningCommands > 0 && !_unitOfWorkActive)
        {
            throw new InvalidOperationException(
                "A command was sent before the Operation's Unit of Work started, from an authorizer or a validator. Only a handler may send a nested command."
            );
        }

        _commandStarted = true;
        return _runningCommands++ == 0;
    }

    internal void ExitCommand() => _runningCommands--;

    internal void StartUnitOfWork() => _unitOfWorkActive = true;

    internal void EndUnitOfWork() => _unitOfWorkActive = false;

    internal void EnterQuery() => _runningQueries++;

    internal void ExitQuery() => _runningQueries--;
}
