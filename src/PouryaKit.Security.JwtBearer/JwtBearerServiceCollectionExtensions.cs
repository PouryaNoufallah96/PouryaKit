using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Security.JwtBearer;

/// <summary>Composes JWT bearer authentication, part of the Security Capability, on a Host's services.</summary>
public static class JwtBearerServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Authenticates callers with standard ASP.NET Core JWT bearer authentication, the default scheme
        /// <see cref="JwtBearerDefaults.AuthenticationScheme"/>, and normalizes each verified token into the caller's
        /// Actor and tenant. Issuer, audience and signing keys are the scheme's own <see cref="JwtBearerOptions"/>, read
        /// from the configuration section <c>Authentication:Schemes:Bearer</c> (the <c>dotnet user-jwts</c> shape) or
        /// set with <c>Configure&lt;JwtBearerOptions&gt;("Bearer", ...)</c>. The token keeps its own claim names; the
        /// value of <see cref="PouryaKitJwtBearerOptions.ActorClaim"/> becomes the identity's
        /// <c>ClaimTypes.NameIdentifier</c> and that of <see cref="PouryaKitJwtBearerOptions.TenantClaim"/> its
        /// <see cref="PouryaKitClaimTypes.Tenant"/>. A token without exactly one Actor value, or with more than one
        /// tenant value, is refused; one without the tenant claim has no tenant.
        /// </summary>
        /// <remarks>
        /// The Host fails when it starts, naming every problem, unless its bearer options validate the issuer against
        /// a configured issuer or authority, require the audience and validate it against a configured audience, and
        /// validate the signature against a signing key or authority and the token's lifetime; the Kit offers no way to
        /// turn one of them off. A custom validator delegate that replaces one of these checks is refused, and so are
        /// events that would skip the normalization: set the application's event delegates on <c>Events</c> with
        /// <c>Configure&lt;JwtBearerOptions&gt;("Bearer", ...)</c>, never replace <c>Events</c> in a PostConfigure,
        /// subclass <see cref="JwtBearerEvents"/> or set <c>EventsType</c>.
        /// </remarks>
        public AuthenticationBuilder AddPouryaKitJwtBearer(Action<PouryaKitJwtBearerOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var claims = services
                .AddOptions<PouryaKitJwtBearerOptions>()
                .Validate(
                    options => !string.IsNullOrWhiteSpace(options.ActorClaim),
                    "PouryaKitJwtBearerOptions.ActorClaim names no claim to identify the Actor."
                )
                .Validate(
                    options => options.TenantClaim is null || !string.IsNullOrWhiteSpace(options.TenantClaim),
                    "PouryaKitJwtBearerOptions.TenantClaim is blank; leave it null when the application has no tenant."
                )
                .ValidateOnStart();
            if (configure is not null)
            {
                claims.Configure(configure);
            }

            var authentication = services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(bearer => bearer.MapInboundClaims = false);
            services
                .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
                .PostConfigure<IOptions<PouryaKitJwtBearerOptions>>(
                    (bearer, names) => VerifiedClaims.NormalizeIn(bearer, names.Value)
                )
                .ValidateOnStart();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<JwtBearerOptions>, JwtBearerValidation>()
            );
            return authentication;
        }
    }
}
