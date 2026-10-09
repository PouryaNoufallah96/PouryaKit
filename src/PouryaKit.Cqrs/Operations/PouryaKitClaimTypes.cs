namespace PouryaKit.Cqrs;

/// <summary>The claim types the Kit issues itself when it normalizes a verified identity.</summary>
public static class PouryaKitClaimTypes
{
    /// <summary>
    /// The caller's verified tenant. <c>AddPouryaKitJwtBearer</c> writes it from the token's configured tenant claim,
    /// and <c>PouryaKit.Web</c> reads the Operation's tenant from it unless <c>PouryaKitWebOptions.TenantClaim</c> names
    /// another claim.
    /// </summary>
    public const string Tenant = "pouryakit:tenant";
}
