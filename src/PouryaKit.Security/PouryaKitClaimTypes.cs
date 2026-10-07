namespace PouryaKit.Security;

/// <summary>The claim types the Kit issues itself when it normalizes a verified identity.</summary>
public static class PouryaKitClaimTypes
{
    /// <summary>
    /// The caller's verified tenant. <c>AddPouryaKitJwtBearer</c> writes it from the token's configured tenant claim;
    /// a Host using <c>PouryaKit.Web</c> names it as <c>PouryaKitWebOptions.TenantClaim</c>.
    /// </summary>
    public const string Tenant = "pouryakit:tenant";
}
