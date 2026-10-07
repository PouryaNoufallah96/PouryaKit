using Microsoft.AspNetCore.Http;

namespace PouryaKit.Web;

// The Idempotency-Key header of an endpoint that requires one: the request id of the Operation the request begins, read
// as the caller sent it, and only on an endpoint that declared it with RequireIdempotencyKey.
internal static class IdempotencyKeyHeader
{
    public const string Name = "Idempotency-Key";

    public static string Read(IHeaderDictionary headers) =>
        headers.TryGetValue(Name, out var values) && values is [{ } value] && OperationContextHttpHeaders.IsHeaderValue(value)
            ? value
            : throw new BadHttpRequestException(
                $"The endpoint requires a single {Name} header that is {OperationContextHttpHeaders.Rule}."
            );
}

/// <summary>Endpoint metadata from <c>RequireIdempotencyKey()</c>.</summary>
internal sealed record IdempotencyKeyRequired;
