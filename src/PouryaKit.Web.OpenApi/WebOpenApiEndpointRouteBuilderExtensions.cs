using Asp.Versioning.ApiExplorer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Scalar.AspNetCore;

namespace PouryaKit.Web.OpenApi;

/// <summary>
/// Serves the OpenAPI document of each API version and the Scalar API reference on a Host's routes, for the API
/// versions and OpenAPI Capability.
/// </summary>
public static class WebOpenApiEndpointRouteBuilderExtensions
{
    extension(IEndpointRouteBuilder routes)
    {
        /// <summary>
        /// Serves the OpenAPI document of each API version the Modules declared, at <c>/openapi/v1.json</c>,
        /// <c>/openapi/v2.json</c> and so on, and the Scalar API reference at <c>/scalar/</c>, which lists every one
        /// of them. Like any endpoint they require an authenticated caller unless the returned builder says otherwise,
        /// such as <c>AllowAnonymous()</c>.
        /// </summary>
        public IEndpointConventionBuilder MapPouryaKitOpenApi()
        {
            ArgumentNullException.ThrowIfNull(routes);

            var documents = routes.MapGroup("");
            documents.MapOpenApi().WithDocumentPerVersion();
            documents.MapScalarApiReference(
                (scalar, httpContext) =>
                {
                    var versions =
                        httpContext.RequestServices.GetRequiredService<IApiVersionDescriptionProvider>();
                    foreach (var version in versions.ApiVersionDescriptions)
                    {
                        scalar.AddDocument(version.GroupName);
                    }
                }
            );
            return documents;
        }
    }
}
