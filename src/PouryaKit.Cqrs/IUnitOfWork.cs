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
    /// further step that can run nested commands; otherwise roll back. Return the command's Result after a commit, and
    /// a failed Result after a rollback: the command's own failure, or <see cref="Operation.RollbackErrors"/>. An
    /// exception from <paramref name="command"/> propagates with nothing committed.
    /// </summary>
    Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken);
}
