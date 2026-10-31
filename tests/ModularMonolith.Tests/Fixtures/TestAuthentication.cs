using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModularMonolith.Host;
using PouryaKit.Cqrs;

namespace ModularMonolith.Tests.Fixtures;

// Stands in for the Host's bearer scheme: a request with "Authorization: Test <id>" is the authenticated person <id>,
// and a request without it is anonymous. It issues the claims the Kit's bearer scheme normalizes a verified token into:
// the Actor's, a tenant's from a "Test-Tenant" header, and a permission claim each from a "Test-Permissions" header,
// comma-separated.
internal sealed class TestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    public const string TenantHeader = "Test-Tenant";

    /// <summary>A <see cref="TenantHeader"/> value that issues the tenant claim with an empty value.</summary>
    public const string BlankTenant = "(blank)";

    public const string PermissionsHeader = "Test-Permissions";

    /// <summary>A caller id that authenticates with no claim naming the person.</summary>
    public const string WithoutActorClaim = "(no actor claim)";

    /// <summary>A caller id whose authentication throws, as a scheme that cannot reach its authority does.</summary>
    public const string Throwing = "(throws)";

    public const string ThrownMessage = "The authentication scheme threw.";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith($"{SchemeName} ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var caller = header[(SchemeName.Length + 1)..];
        if (caller == Throwing)
        {
            throw new InvalidOperationException(ThrownMessage);
        }

        List<Claim> claims =
            caller == WithoutActorClaim
                ? [new Claim(ClaimTypes.Name, caller)]
                : [new Claim(ClaimTypes.NameIdentifier, caller)];
        if (Request.Headers[TenantHeader].ToString() is { Length: > 0 } tenant)
        {
            claims.Add(new Claim(PouryaKitClaimTypes.Tenant, tenant == BlankTenant ? "" : tenant));
        }

        claims.AddRange(
            Request
                .Headers[PermissionsHeader]
                .ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(permission => new Claim(CallerPermissions.ClaimType, permission))
        );

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(
            AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)
            )
        );
    }
}
