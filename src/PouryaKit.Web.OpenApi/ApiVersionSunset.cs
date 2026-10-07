namespace PouryaKit.Web.OpenApi;

/// <summary>When an API version stops being served, and the page that tells its callers what to move to.</summary>
public sealed class ApiVersionSunset
{
    /// <summary>The configured date after which the version is no longer served.</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>The HTML page of the version's sunset policy.</summary>
    public Uri? Link { get; set; }
}
