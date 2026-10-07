namespace PouryaKit.Web.OpenApi;

/// <summary>
/// The Host's configured sunset of each API version its Modules retire, such as the configuration section
/// <c>PouryaKit:OpenApi</c>. Nothing here is derived from the current time.
/// </summary>
public sealed class PouryaKitOpenApiOptions
{
    /// <summary>
    /// The sunset of an API version, keyed by the version as its Modules declare it, such as <c>"1.0"</c>. A version
    /// with a sunset reports it in the <c>Sunset</c> and <c>Link</c> headers of each response it serves and in its
    /// OpenAPI document.
    /// </summary>
    public Dictionary<string, ApiVersionSunset> Sunsets { get; } = [];
}
