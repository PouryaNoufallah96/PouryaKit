namespace PouryaKit.Web;

/// <summary>
/// The Host's named CORS policies, registered with ASP.NET Core's CORS services under their names. The Host applies one
/// to its pipeline with <c>app.UseCors(name)</c>, which also answers preflight requests; a minimal API endpoint's
/// <c>RequireCors(name)</c> does not answer them. A policy allows only what it lists: with no origins it allows no
/// origin, and a name nobody configured allows nothing either. Empty, the default, the Host answers no cross-origin
/// request.
/// </summary>
/// <remarks>
/// The Host fails when it starts if a policy lists an origin that is not <c>scheme://host[:port]</c> as a browser sends
/// it, or a wildcard (<c>*</c>) anywhere: nothing widens a policy beyond what it names.
/// </remarks>
public sealed class PouryaKitCorsOptions
{
    public IDictionary<string, PouryaKitCorsPolicy> Policies { get; } =
        new Dictionary<string, PouryaKitCorsPolicy>(StringComparer.Ordinal);
}

/// <summary>What one named CORS policy allows; each member maps to the same setting of ASP.NET Core's policy.</summary>
public sealed class PouryaKitCorsPolicy
{
    /// <summary>The origins allowed, such as <c>https://app.example</c>; empty allows no origin.</summary>
    public ISet<string> Origins { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The methods a preflight request may ask for.</summary>
    public ISet<string> Methods { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The request headers a preflight request may ask for.</summary>
    public ISet<string> Headers { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The response headers the browser lets the calling script read, such as <c>Location</c>.</summary>
    public ISet<string> ExposedHeaders { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Whether the browser may send credentials, such as cookies, with the request.</summary>
    public bool AllowCredentials { get; set; }

    /// <summary>How long the browser may cache a preflight answer; null leaves it to the browser.</summary>
    public TimeSpan? PreflightMaxAge { get; set; }
}
