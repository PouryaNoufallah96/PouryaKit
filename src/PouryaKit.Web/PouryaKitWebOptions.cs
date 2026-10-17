using System.Security.Claims;
using PouryaKit.Cqrs;

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
    /// it; an application that authenticates another way names the claim its scheme issues. Null or blank, it would fail
    /// every authenticated request, so the Host does not start: it fails naming this option when it starts, or earlier,
    /// where <c>MapPouryaKitModuleEndpoints</c> or <c>BeginOperationPerRequest</c> reads it.
    /// </summary>
    public string ActorClaim { get; set; } = ClaimTypes.NameIdentifier;

    /// <summary>
    /// The claim whose value is the Operation's tenant. The default is <see cref="PouryaKitClaimTypes.Tenant"/>, under
    /// which the Kit's bearer scheme (<c>AddPouryaKitJwtBearer</c>) writes the token's configured tenant claim, so a Host
    /// sets it only to read another claim its own scheme issues, or null to read no tenant. A caller whose identity
    /// lacks the claim, or carries it blank, runs with a null tenant and is not refused: some callers legitimately have
    /// no tenant, and what a null tenant may see is the tenant filter's business, not the HTTP boundary's. The option
    /// itself is null or names a claim: blank, it would give every caller a null tenant unnoticed, so the Host does not
    /// start, as for a blank <see cref="ActorClaim"/>.
    /// </summary>
    public string? TenantClaim { get; set; } = PouryaKitClaimTypes.Tenant;
}
