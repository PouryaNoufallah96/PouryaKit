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
    /// <summary>The policies, each under the name the Host applies it by.</summary>
    public IDictionary<string, PouryaKitCorsPolicy> Policies { get; } =
        new Dictionary<string, PouryaKitCorsPolicy>(StringComparer.Ordinal);
}
