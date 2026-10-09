namespace PouryaKit.Web.OpenApi;

/// <summary>
/// The sunset an API version reports to its callers: the date it is announced to end on, and the page that tells them
/// what to move to. The Kit reports it and keeps serving the version; a Module ends a version by no longer declaring it.
/// </summary>
public sealed class ApiVersionSunset
{
    /// <summary>The configured date the version is announced to end on. It is reported, not enforced.</summary>
    public DateTimeOffset? Date { get; set; }

    /// <summary>The HTML page of the version's sunset policy.</summary>
    public Uri? Link { get; set; }
}
