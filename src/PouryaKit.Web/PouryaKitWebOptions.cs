using System.Security.Claims;

namespace PouryaKit.Web;

/// <summary>
/// Which claims of the authenticated caller fill the <c>OperationContext</c>. Nothing here chooses an identity
/// provider: the application configures its own authentication and names the claims it issues.
/// </summary>
public sealed class PouryaKitWebOptions
{
    /// <summary>
    /// The claim whose value is the <c>Actor.Person</c> id. The Kit's bearer scheme (<c>AddPouryaKitJwtBearer</c>)
    /// writes the configured Actor claim's value under <see cref="ClaimTypes.NameIdentifier"/>, so the default fits
    /// it; an application that authenticates another way names the claim its scheme issues.
    /// </summary>
    public string ActorClaim { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>
    /// The claim whose value is the Operation's tenant; null, the default, when the application has none. A caller
    /// whose identity lacks the claim, or carries it blank, runs with a null tenant and is not refused: some callers
    /// legitimately have no tenant, and what a null tenant may see is the tenant filter's business, not the HTTP
    /// boundary's.
    /// </summary>
    public string? TenantClaim { get; set; }
}
