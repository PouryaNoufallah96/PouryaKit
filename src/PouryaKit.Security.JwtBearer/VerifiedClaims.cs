using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using PouryaKit.Cqrs;

namespace PouryaKit.Security.JwtBearer;

// Runs on each token the handler has verified, before the application's own OnTokenValidated, which it keeps. The Kit's
// claims are written only from the configured names: one the token itself carries under a Kit type is dropped first,
// so no issued claim stands in for the configured one. The start check finds it installed by the delegate's target.
internal sealed class VerifiedClaims(
    PouryaKitJwtBearerOptions names,
    Func<TokenValidatedContext, Task> next
)
{
    public static void NormalizeIn(JwtBearerOptions bearer, PouryaKitJwtBearerOptions names)
    {
        var events = bearer.Events ??= new JwtBearerEvents();
        events.OnTokenValidated = new VerifiedClaims(
            names,
            events.OnTokenValidated
        ).OnTokenValidated;
    }

    // Exactly JwtBearerEvents: a subclass may override TokenValidated and never call OnTokenValidated.
    public static bool IsInstalledIn(JwtBearerEvents? events) =>
        events?.GetType() == typeof(JwtBearerEvents)
        && events.OnTokenValidated?.Target is VerifiedClaims;

    private Task OnTokenValidated(TokenValidatedContext context)
    {
        if (Normalize(context.Principal?.Identity as ClaimsIdentity, names) is { } refusal)
        {
            context.Fail(refusal);
            return Task.CompletedTask;
        }

        return next(context);
    }

    private static string? Normalize(ClaimsIdentity? identity, PouryaKitJwtBearerOptions names)
    {
        if (identity is null)
        {
            return "The verified token has no identity.";
        }

        if (Values(identity, names.ActorClaim) is not [var actor])
        {
            return $"The token carries no single {names.ActorClaim} claim to identify the Actor.";
        }

        List<Claim> tenants = names.TenantClaim is { } tenantClaim
            ? Values(identity, tenantClaim)
            : [];
        if (tenants.Count > 1)
        {
            return $"The token carries more than one {names.TenantClaim} claim.";
        }

        var issued = identity.FindAll(claim =>
            claim.Type is ClaimTypes.NameIdentifier or PouryaKitClaimTypes.Tenant
        );
        foreach (var claim in issued.ToList())
        {
            identity.TryRemoveClaim(claim);
        }

        identity.AddClaim(Renamed(actor, ClaimTypes.NameIdentifier));
        if (tenants is [var tenant])
        {
            identity.AddClaim(Renamed(tenant, PouryaKitClaimTypes.Tenant));
        }

        return null;
    }

    private static List<Claim> Values(ClaimsIdentity identity, string type) =>
        [.. identity.FindAll(type).Where(claim => !string.IsNullOrWhiteSpace(claim.Value))];

    private static Claim Renamed(Claim claim, string type) =>
        new(type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer);
}
