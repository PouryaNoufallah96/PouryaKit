using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Localization;

/// <summary>
/// Composes the Localization Capability on a Module's or a Host's services: English and Persian, with the request
/// culture read from the client's <c>Accept-Language</c> header.
/// </summary>
public static class LocalizationServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Localizes the Features in <paramref name="featureAssemblies"/>: a Feature writes a localized message through
        /// the framework's <c>IStringLocalizer&lt;T&gt;</c>, which reads the <c>.resx</c> resources compiled into the
        /// assembly of <c>T</c> beside it, the neutral ones in English and those named <c>.fa.resx</c> in Persian. Each
        /// request runs in the culture of the client's <c>Accept-Language</c> header when it is English or Persian, a
        /// regional variant such as <c>fa-IR</c> included, and in English otherwise; no other source sets it. The
        /// request culture is set ahead of every middleware the application adds, so the Host adds none, and the Kit's
        /// own Error messages stay English in every culture. Named with no assembly on a Module's builder
        /// (<see cref="IModuleServices"/>), it records the assembly of the Module's definition for the Host; named
        /// assemblies replace it. A second call, from another Module or the Host's own services, adds no second
        /// culture or middleware.
        /// </summary>
        public IServiceCollection AddPouryaKitLocalization(params Assembly[] featureAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            IModuleServices.FeatureAssembliesOf(services, featureAssemblies);
            services.AddLocalization();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigureOptions<RequestLocalizationOptions>, HeaderRequestCulture>()
            );
            services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, HeaderRequestCulture>());
            return services;
        }
    }
}
