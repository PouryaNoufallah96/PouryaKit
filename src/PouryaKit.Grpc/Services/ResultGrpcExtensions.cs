using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grpc.Core;
using PouryaKit.Domain;

namespace PouryaKit.Grpc;

/// <summary>
/// The one place a <see cref="Result"/> becomes a gRPC answer. A success is the response the service makes of it; a
/// failure is an <see cref="RpcException"/>, gRPC's way to answer a status, which Grpc.AspNetCore writes as that status
/// with every Error, in order, by code and message, with the target of a validation Error, in the binary trailer
/// <see cref="ErrorsTrailer"/> in the Kit's JSON, the entries an HTTP problem response's <c>errors</c> member lists.
/// </summary>
/// <remarks>
/// The status comes from the Errors' kinds: validation <see cref="StatusCode.InvalidArgument"/>, not found
/// <see cref="StatusCode.NotFound"/>, conflict <see cref="StatusCode.Aborted"/>, forbidden
/// <see cref="StatusCode.PermissionDenied"/>, anything else <see cref="StatusCode.Internal"/>. When the Errors are of
/// several kinds, the most severe decides, in the order HTTP's status follows: unexpected, forbidden, not found, conflict,
/// validation. The status's message is the deciding Error's.
/// </remarks>
public static class ResultGrpcExtensions
{
    /// <summary>The binary trailer that carries a failed Result's Errors as a JSON array of code, message and target.</summary>
    public const string ErrorsTrailer = "pouryakit-errors-bin";

    private static readonly JsonSerializerOptions Json = new JsonSerializerOptions().UsePouryaKitJson();

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
        /// <summary><paramref name="response"/> on success; on failure throws the status and the Errors' trailer.</summary>
        /// <exception cref="RpcException">The Result failed.</exception>
        public TResponse ToGrpcResponse<TResponse>(TResponse response)
        {
            ArgumentNullException.ThrowIfNull(result);
            return result.IsSuccess ? response : throw Failure(result.Errors);
        }
    }

    extension<T>(Result<T> result)
        where T : notnull
    {
        /// <summary>
        /// The response <paramref name="onSuccess"/> makes of the value on success, such as
        /// <c>product => new ProductReply { Name = product.Name }</c>; on failure throws the status and the Errors'
        /// trailer.
        /// </summary>
        /// <exception cref="RpcException">The Result failed.</exception>
        public TResponse ToGrpcResponse<TResponse>(Func<T, TResponse> onSuccess)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(onSuccess);
            return result.IsSuccess ? onSuccess(result.Value) : throw Failure(result.Errors);
        }
    }

    private static RpcException Failure(ImmutableArray<Error> errors)
    {
        var deciding = errors.MaxBy(error => Array.IndexOf(KindsBySeverity, error.Kind))!;
        return new RpcException(
            new Status(StatusOf(deciding.Kind), deciding.Message),
            new Metadata { { ErrorsTrailer, JsonSerializer.SerializeToUtf8Bytes(errors.Select(GrpcError.Of).ToList(), Json) } }
        );
    }

    private static StatusCode StatusOf(ErrorKind kind) =>
        kind switch
        {
            ErrorKind.Validation => StatusCode.InvalidArgument,
            ErrorKind.NotFound => StatusCode.NotFound,
            ErrorKind.Conflict => StatusCode.Aborted,
            ErrorKind.Forbidden => StatusCode.PermissionDenied,
            _ => StatusCode.Internal,
        };

    private sealed record GrpcError(
        string Code,
        string Message,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Target
    )
    {
        public static GrpcError Of(Error error) => new(error.Code, error.Message, error.Target);
    }
}
