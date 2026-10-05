using System.Collections.Immutable;
using FluentValidation;
using FluentValidation.Results;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Validation;

// Runs every validator of the operation and returns all of their failures as one validation Result; it never throws
// for invalid input.
internal sealed class FluentValidationStep<TOperation>(IEnumerable<IValidator<TOperation>> validators)
    : IValidationStep<TOperation>
    where TOperation : notnull
{
    private const string FallbackCode = "Validation.Failed";

    public async Task<Result> ValidateAsync(TOperation operation, CancellationToken cancellationToken)
    {
        var errors = ImmutableArray.CreateBuilder<Error>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(operation, cancellationToken);
            errors.AddRange(result.Errors.Select(ToError));
        }

        return errors.Count == 0 ? Result.Success() : errors.ToImmutable();
    }

    private static Error ToError(ValidationFailure failure)
    {
        var code = string.IsNullOrWhiteSpace(failure.ErrorCode) ? FallbackCode : failure.ErrorCode;
        var message = failure.ErrorMessage ?? "";
        return string.IsNullOrWhiteSpace(failure.PropertyName)
            ? Error.Validation(code, message)
            : Error.Validation(code, message, failure.PropertyName);
    }
}
