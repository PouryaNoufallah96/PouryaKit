using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The optional authorization hook for one command or query type, run before validation. Nothing requires one: with
/// none, the step does nothing. The Actor is authorized at the external entry point; use this only for a rule that
/// belongs to the command or query itself.
/// </summary>
public interface IAuthorizer<in TOperation>
    where TOperation : notnull
{
    /// <summary>A success allows the Operation; a failed Result, normally a forbidden Error, stops it.</summary>
    Task<Result> AuthorizeAsync(TOperation operation, CancellationToken cancellationToken);
}
