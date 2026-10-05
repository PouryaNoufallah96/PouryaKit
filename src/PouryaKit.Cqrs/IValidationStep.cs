using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The validation step of the pipeline for one command or query type. <c>PouryaKit.Validation</c> supplies it from
/// the Feature's validators; without that package none is registered and the step does nothing. Feature code writes
/// validators, never this.
/// </summary>
public interface IValidationStep<in TOperation>
    where TOperation : notnull
{
    /// <summary>A success when the operation is valid; otherwise one failed Result holding every validation Error.</summary>
    Task<Result> ValidateAsync(TOperation operation, CancellationToken cancellationToken);
}
