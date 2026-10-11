using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The single owner of an Operation's outcome. The dispatcher calls it once per Operation, for the outermost command
/// only: a nested command joins it by running inside that call, and a query never reaches it. Feature code never
/// calls it.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs the outermost command of the Operation and keeps or discards everything it changed. Commit only when the
    /// command's Result is a success and <see cref="Operation.IsRollbackOnly"/> is false, checked again after any
    /// further step that can run nested commands; otherwise roll back. A store that refuses the save for a reason the
    /// caller can act on, such as a concurrent change to the same data or a violation of an explicitly mapped unique
    /// constraint, also ends in a rollback; the Unit of Work then fails with an Error naming that reason, and retries
    /// nothing. Return the command's Result after a commit, and a failed Result after a rollback: the command's own failure,
    /// <see cref="Operation.RollbackErrors"/>, or the refused save's Error. An exception from
    /// <paramref name="command"/>, or any other failure before the commit, propagates with nothing committed.
    /// A commit exception propagates too, but a lost acknowledgement can leave its outcome unknown to the caller.
    /// Preparing the save also throws when it finds a bug: an auditable or soft-deletable change in an Operation with no
    /// <see cref="OperationContext"/>, or a change to a part of an aggregate the Operation did not load.
    /// Returning any other success is a bug the dispatcher reports with an <see cref="InvalidOperationException"/>.
    /// </summary>
    Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken);
}
