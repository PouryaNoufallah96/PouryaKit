using System.Globalization;
using Asp.Versioning;
using Asp.Versioning.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Web.OpenApi;

public static class WebOpenApiServiceCollectionExtensions
{
    /// <summary>The request header a caller names the API version in, on a Module's unversioned paths.</summary>
    public const string ApiVersionHeader = "api-version";

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers API versioning and one OpenAPI document per supported API version for the Host. A request names
        /// its version in the URL segment of a Module's versioned paths (<c>/catalog/v2/products</c>) or in the
        /// <see cref="ApiVersionHeader"/> header on its unversioned paths (<c>/catalog/products</c>); naming none
        /// there, or two that differ, is refused with a 400 problem response. Every response reports the versions its
        /// Module supports and deprecates, and a version with a sunset in <see cref="PouryaKitOpenApiOptions"/> reports
        /// that date and its policy link. The documents are the versions the Modules declared with
        /// <c>AddPouryaKitApiVersions</c>; in each, an operation that requires an authenticated caller requires the
        /// HTTP bearer scheme, and an operation that allows anonymous callers requires none. <c>MapPouryaKitOpenApi</c>
        /// serves them.
        /// </summary>
        public IServiceCollection AddPouryaKitOpenApi(Action<PouryaKitOpenApiOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var options = services.AddOptions<PouryaKitOpenApiOptions>();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            services
                .AddApiVersioning(versioning =>
                {
                    versioning.ApiVersionReader = ApiVersionReader.Combine(
                        new UrlSegmentApiVersionReader(),
                        new HeaderApiVersionReader(ApiVersionHeader)
                    );
                    versioning.ReportApiVersions = true;
                })
                .AddApiExplorer(explorer =>
                {
                    explorer.GroupNameFormat = "'v'VVV";
                    explorer.SubstituteApiVersionInUrl = true;
                })
                .AddOpenApi(document =>
                {
                    document.Document.AddDocumentTransformer<BearerSecurity>();
                    document.Document.AddOperationTransformer<BearerSecurity>();
                    // The library's own notices say "was" or "will be" by comparing the date with the current time.
                    document.DocumentDescription.DeprecationNotice = _ => "This API version is deprecated.";
                    document.DocumentDescription.SunsetNotice = sunset =>
                        sunset.Date is { } date
                            ? $"Its sunset date is {date.ToString("R", CultureInfo.InvariantCulture)}."
                            : null;
                });
            services
                .AddOptions<ApiVersioningOptions>()
                .Configure<IOptions<PouryaKitOpenApiOptions>>((versioning, kit) => AddSunsets(versioning, kit.Value));
            return services;
        }

        /// <summary>
        /// Declares the API versions of the Module whose endpoints are mapped under <paramref name="routePrefix"/>, the
        /// prefix its <c>AddPouryaKitEndpoints</c> names: <paramref name="declare"/> states them on the Module's API
        /// version set, such as <c>versions.HasDeprecatedApiVersion(new ApiVersion(1, 0)).HasApiVersion(new
        /// ApiVersion(2, 0))</c>. Each endpoint of the Module serves every declared version, both under
        /// <c>{routePrefix}/v{version}</c> and under <paramref name="routePrefix"/> with the version in the
        /// <see cref="WebOpenApiServiceCollectionExtensions.ApiVersionHeader"/> header, unless it maps itself to some
        /// with <c>MapToApiVersion</c>. A Module's Composition Root calls it; the Host registers
        /// <c>AddPouryaKitOpenApi</c>, or mapping the Module's endpoints throws. A Module that declares no version
        /// keeps its unversioned paths.
        /// </summary>
        public IServiceCollection AddPouryaKitApiVersions(string routePrefix, Action<ApiVersionSetBuilder> declare)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(routePrefix);
            ArgumentNullException.ThrowIfNull(declare);

            if (
                services.Any(descriptor =>
                    descriptor.ImplementationInstance is ModuleApiVersions declared && declared.RoutePrefix == routePrefix
                )
            )
            {
                throw new InvalidOperationException($"The API versions of the route group {routePrefix} are already declared.");
            }

            services.AddSingleton(new ModuleApiVersions(routePrefix, declare));
            services.TryAddSingleton<IModuleRouteGroups, VersionedModuleRouteGroups>();
            return services;
        }
    }

    private static void AddSunsets(ApiVersioningOptions versioning, PouryaKitOpenApiOptions kit)
    {
        foreach (var (configured, sunset) in kit.Sunsets)
        {
            if (!ApiVersionParser.Default.TryParse(configured, out var version))
            {
                throw new InvalidOperationException(
                    $"The sunset of API version '{configured}' names no API version. Key each sunset by a version such as \"1.0\"."
                );
            }

            var policy = versioning.Policies.Sunset(version);
            if (sunset.Date is { } date)
            {
                policy.Effective(date);
            }

            if (sunset.Link is { } link)
            {
                policy.Link(link).Type("text/html");
            }
        }
    }
}

/// <summary>The API versions one Module declared for the route group its endpoints are mapped under.</summary>
internal sealed record ModuleApiVersions(string RoutePrefix, Action<ApiVersionSetBuilder> Declare);
