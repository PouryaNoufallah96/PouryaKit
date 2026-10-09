namespace PouryaKit.Security.JwtBearer;

/// <summary>
/// The names of the verified token's claims that identify the caller, as the identity provider issues them. Issuer,
/// audience and signing keys are the standard <c>JwtBearerOptions</c>, read from <c>Authentication:Schemes:Bearer</c>.
/// </summary>
public sealed class PouryaKitJwtBearerOptions
{
    /// <summary>The claim whose value is the <c>Actor.Person</c> id; by default <c>"sub"</c>, the token's subject.</summary>
    public string ActorClaim { get; set; } = "sub";

    /// <summary>
    /// The claim whose value is the caller's tenant; null, the default, when the application has none. A token without
    /// it runs with a null tenant and is not refused.
    /// </summary>
    public string? TenantClaim { get; set; }
}
