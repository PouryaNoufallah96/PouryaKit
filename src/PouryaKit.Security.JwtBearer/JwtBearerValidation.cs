using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace PouryaKit.Security.JwtBearer;

// Checks the bearer scheme's options as they end, after every Configure and PostConfigure, so a setting the
// configuration binder or the application changed later cannot weaken them unnoticed. An authority or metadata address
// supplies the issuer and the signing keys from its discovery document.
internal sealed class JwtBearerValidation : IValidateOptions<JwtBearerOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme)
        {
            return ValidateOptionsResult.Skip;
        }

        var token = options.TokenValidationParameters;
        var discovered = options.ConfigurationManager is not null;
        List<string> failures = [];
        if (!token.ValidateAudience || !Named(token.ValidAudience, token.ValidAudiences))
        {
            failures.Add(
                "The bearer scheme must validate the audience against a configured audience: set ValidAudience or ValidAudiences in Authentication:Schemes:Bearer."
            );
        }

        if (!token.ValidateIssuer || !(discovered || Named(token.ValidIssuer, token.ValidIssuers)))
        {
            failures.Add(
                "The bearer scheme must validate the issuer against a configured issuer: set ValidIssuer, ValidIssuers or Authority in Authentication:Schemes:Bearer."
            );
        }

        var keyed =
            discovered
            || token.IssuerSigningKey is not null
            || token.IssuerSigningKeys?.Any() == true
            || token.IssuerSigningKeyResolver is not null;
        if (!token.RequireSignedTokens || !keyed)
        {
            failures.Add(
                "The bearer scheme must validate the signature against a signing key: set SigningKeys for the issuer, or Authority, in Authentication:Schemes:Bearer."
            );
        }

        if (!token.ValidateLifetime || !token.RequireExpirationTime)
        {
            failures.Add("The bearer scheme must validate the token's lifetime and require its expiration time.");
        }

        if (options.MapInboundClaims)
        {
            failures.Add(
                "The bearer scheme maps inbound claims; the Kit reads the token's own claim names, so leave MapInboundClaims false."
            );
        }

        if (options.EventsType is not null)
        {
            failures.Add(
                "The bearer scheme resolves its events from EventsType, which bypasses the Kit's claim normalization; set Events instead."
            );
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool Named(string? one, IEnumerable<string>? many) =>
        !string.IsNullOrWhiteSpace(one) || many?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;
}
