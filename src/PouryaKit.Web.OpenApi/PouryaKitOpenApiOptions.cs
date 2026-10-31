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
    /// OpenAPI document. The Host does not start, and the failure names the key, when a key names no API version, two
    /// keys name one version (<c>"1"</c> and <c>"1.0"</c>), a <c>Link</c> is not an absolute URI, or a sunset has neither
    /// a <c>Date</c> nor a <c>Link</c>. A date in the past stays valid, since nothing reads the clock, and so does a key
    /// no Module declares, whose versions are known only once the endpoints are mapped. Bound from configuration, an entry
    /// whose <c>Date</c> or <c>Link</c> the binder cannot convert is left out without an error, so its version reports no
    /// sunset: the binder skips a dictionary entry it fails to bind unless the Host sets
    /// <c>BinderOptions.ErrorOnUnknownConfiguration</c>. Keys are compared ordinally, so <c>"1.0-Beta"</c> and
    /// <c>"1.0-beta"</c>, which name one version, are two keys the Host refuses rather than one replacing the other.
    /// </summary>
    public IDictionary<string, ApiVersionSunset> Sunsets { get; } =
        new Dictionary<string, ApiVersionSunset>(StringComparer.Ordinal);
}
