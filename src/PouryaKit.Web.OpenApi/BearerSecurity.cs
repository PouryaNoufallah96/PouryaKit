using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PouryaKit.Web.OpenApi;

// An operation requires a scheme exactly when the authorization middleware would challenge an anonymous caller with
// it: the operation allows no anonymous caller, its authorize data, or else the fallback policy, give it a policy, and
// the scheme is one the policy names, or else the Host's default authenticate scheme. Only a JWT bearer scheme has an
// OpenAPI form here, HTTP bearer under the scheme's own name; any other scheme is left out of the document, so an
// operation protected by a cookie, an API key or another handler carries no requirement.
internal sealed class BearerSecurity(
    IAuthorizationPolicyProvider policies,
    IAuthenticationSchemeProvider schemes
) : IOpenApiOperationTransformer
{
    // Matched by name: JwtBearer is a NuGet package of the Host's, not part of the shared framework this one references.
    private const string JwtBearerHandler =
        "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";

    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return;
        }

        var policy = await AuthorizationPolicy.CombineAsync(
            policies,
            metadata.OfType<IAuthorizeData>(),
            metadata.OfType<AuthorizationPolicy>()
        );
        if (policy is null)
        {
            return;
        }

        IEnumerable<string> names =
            policy.AuthenticationSchemes.Count > 0 ? policy.AuthenticationSchemes
            : await schemes.GetDefaultAuthenticateSchemeAsync() is { } defaultScheme
                ? [defaultScheme.Name]
            : [];
        foreach (var name in names.Distinct())
        {
            if (
                await schemes.GetSchemeAsync(name) is not { } scheme
                || !IsJwtBearer(scheme.HandlerType)
            )
            {
                continue;
            }

            var document = context.Document!;
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??=
                new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[name] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
            };
            // One requirement per scheme: the policy is met by any of them.
            operation.Security ??= [];
            operation.Security.Add(
                new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(name, document)] = [],
                }
            );
        }
    }

    private static bool IsJwtBearer(Type handler)
    {
        for (Type? type = handler; type is not null; type = type.BaseType)
        {
            if (type.FullName == JwtBearerHandler)
            {
                return true;
            }
        }

        return false;
    }
}
