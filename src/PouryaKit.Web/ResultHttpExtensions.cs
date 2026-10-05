using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PouryaKit.Domain;

namespace PouryaKit.Web;

/// <summary>
/// The one place a <see cref="Result"/> becomes an HTTP response. A success takes the shape the endpoint chooses; a
/// failure is an RFC 9457 problem response whose <c>errors</c> member lists every Error, in order, by code and message,
/// with the target of a validation Error.
/// </summary>
/// <remarks>
/// The status comes from the Errors' kinds: validation 400, not found 404, conflict 409, forbidden 403, anything else
/// 500. When the Errors are of several kinds, the most severe decides, in the order unexpected, forbidden, not found,
/// conflict, validation: the caller cannot succeed by correcting its input while a more severe reason stands.
/// </remarks>
public static class ResultHttpExtensions
{
    private static readonly ErrorKind[] KindsBySeverity =
    [
        ErrorKind.Validation,
        ErrorKind.Conflict,
        ErrorKind.NotFound,
        ErrorKind.Forbidden,
        ErrorKind.Unexpected,
    ];

    extension(Result result)
    {
        /// <summary>204 No Content on success; a problem response on failure.</summary>
        public IResult ToHttpResult()
        {
            ArgumentNullException.ThrowIfNull(result);
            return result.IsSuccess ? TypedResults.NoContent() : Problem(result.Errors);
        }
    }

    extension<T>(Result<T> result)
        where T : notnull
    {
        /// <summary>200 OK with the value as the body on success; a problem response on failure.</summary>
        public IResult ToHttpResult() => result.ToHttpResult(value => TypedResults.Ok(value));

        /// <summary>
        /// The response <paramref name="onSuccess"/> makes of the value on success, such as
        /// <c>id => TypedResults.Created($"/catalog/products/{id}", new { id })</c>; a problem response on failure.
        /// </summary>
        public IResult ToHttpResult(Func<T, IResult> onSuccess)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(onSuccess);
            return result.IsSuccess ? onSuccess(result.Value) : Problem(result.Errors);
        }
    }

    private static ProblemHttpResult Problem(ImmutableArray<Error> errors) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = Status(errors.MaxBy(error => Array.IndexOf(KindsBySeverity, error.Kind))!.Kind),
                Extensions = { ["errors"] = errors.Select(ProblemError.Of).ToList() },
            }
        );

    private static int Status(ErrorKind kind) =>
        kind switch
        {
            ErrorKind.Validation => StatusCodes.Status400BadRequest,
            ErrorKind.NotFound => StatusCodes.Status404NotFound,
            ErrorKind.Conflict => StatusCodes.Status409Conflict,
            ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status500InternalServerError,
        };

    private sealed record ProblemError(
        string Code,
        string Message,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target
    )
    {
        public static ProblemError Of(Error error) => new(error.Code, error.Message, error.Target);
    }
}
