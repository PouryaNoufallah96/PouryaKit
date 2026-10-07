namespace PouryaKit.Web;

/// <summary>
/// The endpoint metadata <c>RequireIdempotencyKey()</c> adds: every request to the endpoint carries one
/// <see cref="Header"/> header whose value is <see cref="Rule"/>. It is public so that <c>PouryaKit.Web.OpenApi</c> can
/// declare that header in the endpoint's OpenAPI operation; an endpoint states it with <c>RequireIdempotencyKey()</c>,
/// never by adding this metadata itself.
/// </summary>
public sealed class IdempotencyKeyRequirement
{
    /// <summary>The header's name.</summary>
    public const string Header = "Idempotency-Key";

    internal IdempotencyKeyRequirement() { }

    /// <summary>What the header's one value is: 1 to 256 visible ASCII characters, with spaces only between them.</summary>
    public static string Rule { get; } = OperationContextHttpHeaders.Rule;
}
