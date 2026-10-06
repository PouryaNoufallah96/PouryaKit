using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ModularMonolith.Tests;

// Stands in for the Host's JwtBearer: a request with "Authorization: Test <id>" is the authenticated person <id>, and
// a request without it is anonymous. The claim it issues is the one JwtBearer maps a token's subject to; a
// "Test-Tenant" header adds a tenant claim.
internal sealed class TestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    public const string TenantClaim = "tenant";

    public const string TenantHeader = "Test-Tenant";

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
            caller == WithoutActorClaim ? [new Claim(ClaimTypes.Name, caller)] : [new Claim(ClaimTypes.NameIdentifier, caller)];
        if (Request.Headers[TenantHeader].ToString() is { Length: > 0 } tenant)
        {
            claims.Add(new Claim(TenantClaim, tenant));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
