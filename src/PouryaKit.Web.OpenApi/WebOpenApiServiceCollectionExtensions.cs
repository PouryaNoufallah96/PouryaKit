using System.Globalization;
using Asp.Versioning;
using Asp.Versioning.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Web.OpenApi;

/// <summary>
/// Composes the API versions and OpenAPI Capability on a Host's services: API versioning, each Module's API versions,
/// and one OpenAPI document per version.
/// </summary>
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
        /// that date and its policy link; a sunset the Kit refuses stops the Host at start, naming its key
        /// (<see cref="PouryaKitOpenApiOptions.Sunsets"/>). The documents are the versions the Modules declared with
        /// <c>AddPouryaKitApiVersions</c>; in each, an operation that requires an authenticated caller requires the
        /// authentication schemes its policy names, or else the Host's default one, as far as each is a JWT bearer
        /// scheme, which the document states as HTTP bearer; a scheme of any other handler is left out, and an operation
        /// that allows anonymous callers requires none. An operation whose endpoint calls <c>RequireIdempotencyKey()</c>
        /// declares the required <c>Idempotency-Key</c> header. <c>MapPouryaKitOpenApi</c> serves them. A second call
        /// adds only its <paramref name="configure"/>.
        /// </summary>
        public IServiceCollection AddPouryaKitOpenApi(Action<PouryaKitOpenApiOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var options = services.AddOptions<PouryaKitOpenApiOptions>().ValidateOnStart();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            // Asp.Versioning's AddOpenApi appends its delegate on every call: a second one would run each transformer twice.
            if (services.Any(descriptor => descriptor.ImplementationType == typeof(SunsetValidation)))
            {
                return services;
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PouryaKitOpenApiOptions>, SunsetValidation>());

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
                    document.Document.AddOperationTransformer<BearerSecurity>();
                    document.Document.AddOperationTransformer<IdempotencyKeyParameter>();
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
        /// <c>AddPouryaKitOpenApi</c>, or mapping the Module's endpoints throws, and a prefix no Module's endpoints are
        /// mapped under fails the Host's start, among the Kit's startup checks (<c>AddPouryaKitCompositionChecks</c>).
        /// An endpoint's name (<c>WithName</c>) stays on its versioned path only. A Module that declares no version keeps
        /// its unversioned paths.
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
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupCheck, UnmappedApiVersionsCheck>());
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

    // AddSunsets reads the sunsets only when Asp.Versioning first reads its options, as endpoints are built: at the first
    // request, or never. This refuses at start what it would throw on, merge into one policy or write as a wrong link, so
    // its throw stays a backstop a validated Host never reaches.
    private sealed class SunsetValidation : IValidateOptions<PouryaKitOpenApiOptions>
    {
        public ValidateOptionsResult Validate(string? name, PouryaKitOpenApiOptions options)
        {
            List<string> failures = [];
            List<(string Key, ApiVersion Version)> versions = [];
            foreach (var (key, sunset) in options.Sunsets)
            {
                if (ApiVersionParser.Default.TryParse(key, out var version))
                {
                    versions.Add((key, version));
                }
                else
                {
                    failures.Add(
                        $"PouryaKitOpenApiOptions.Sunsets has the key '{key}', which names no API version; key each sunset by an API version as the Modules declare it, such as \"1.0\"."
                    );
                }

                if (sunset.Link is { IsAbsoluteUri: false } link)
                {
                    failures.Add(
                        $"PouryaKitOpenApiOptions.Sunsets['{key}'].Link is '{link.OriginalString}', which is not an absolute URI; set the full address of the version's sunset policy page, such as https://example.com/api-v1-sunset."
                    );
                }

                if (sunset is { Date: null, Link: null })
                {
                    failures.Add(
                        $"PouryaKitOpenApiOptions.Sunsets['{key}'] has neither a Date nor a Link; set the date the version is announced to end on, the page of its sunset policy, or both, or remove the entry."
                    );
                }
            }

            // Compared as parsed versions, as Asp.Versioning keys its sunset policies: "1" and "1.0" are one version.
            failures.AddRange(
                versions
                    .GroupBy(entry => entry.Version, entry => $"'{entry.Key}'")
                    .Where(keys => keys.Count() > 1)
                    .Select(keys =>
                        $"PouryaKitOpenApiOptions.Sunsets has the keys {string.Join(" and ", keys)}, which name one API version; keep one sunset per version."
                    )
            );
            return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
        }
    }
}

/// <summary>The API versions one Module declared for the route group its endpoints are mapped under.</summary>
internal sealed record ModuleApiVersions(string RoutePrefix, Action<ApiVersionSetBuilder> Declare);
