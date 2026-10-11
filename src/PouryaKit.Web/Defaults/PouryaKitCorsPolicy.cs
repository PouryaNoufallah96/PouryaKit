namespace PouryaKit.Web;

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
