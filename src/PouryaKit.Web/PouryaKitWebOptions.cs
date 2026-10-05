using System.Security.Claims;

namespace PouryaKit.Web;

/// <summary>
/// Which claims of the authenticated caller fill the <c>OperationContext</c>. Nothing here chooses an identity
/// provider: the application configures its own authentication and names the claims it issues.
/// </summary>
public sealed class PouryaKitWebOptions
{
    /// <summary>
    /// The claim whose value is the <c>Actor.Person</c> id. JwtBearer maps a token's <c>sub</c> to this claim type by
    /// default; set <c>"sub"</c> when the scheme keeps the token's claim names.
    /// </summary>
    public string ActorClaim { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>The claim whose value is the Operation's tenant; null, the default, when the application has none.</summary>
    public string? TenantClaim { get; set; }
}
