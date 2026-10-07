using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace PouryaKit.Web.OpenApi;

// An operation requires the bearer scheme exactly when the authorization middleware would challenge an anonymous
// caller: it allows no anonymous caller, and its authorize data, or else the fallback policy, give it a policy.
internal sealed class BearerSecurity(IAuthorizationPolicyProvider policies)
    : IOpenApiDocumentTransformer,
        IOpenApiOperationTransformer
{
    private const string SchemeName = "Bearer";

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken
    )
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
        };
        return Task.CompletedTask;
    }

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

        operation.Security ??= [];
        operation.Security.Add(
            new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = [] }
        );
    }
}
