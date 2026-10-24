using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Core;

namespace PouryaKit.Cqrs;

/// <summary>Composes the tenant requirement of the Multi-tenancy Capability on a Module's or a Host's services.</summary>
public static class TenancyServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes the Module whose Composition Root calls it tenant-scoped, or, on the Host's own services, the
        /// application: every command and query in <paramref name="featureAssemblies"/>, internal types included, and
        /// every one a handler there handles wherever it is declared, such as in a Module's Contracts, runs only with a
        /// tenant, and one whose Operation has none fails with <see cref="TenantErrors.Required"/> before its validator
        /// and handler run. The requirement is an authorizer on the pipeline's authorization hook, and unlike
        /// a permission check it runs for a trusted continuation too (spec #115): a continuation runs with the tenant it
        /// was given, or not at all. An operation's authorizers run in the order registered, so declare it before
        /// <c>AddPouryaKitCqrs</c> and <c>AddPouryaKitPermissions</c> to have it run first. The declaration is recorded
        /// for the Host, where <see cref="HostComposition.IsTenantScoped"/> reads it. Named with no assembly on a
        /// Module's builder (<see cref="IModuleServices"/>), it scans the assembly of the Module's definition; named
        /// assemblies replace it. Throws on the Host's own services when it names none. The aggregates such a Module
        /// keeps per tenant implement <c>ITenantScoped</c>, whose rows EF Core reads only for the Operation's tenant.
        /// </summary>
        public IServiceCollection AddPouryaKitTenancy(params Assembly[] featureAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            var assemblies = IModuleServices.FeatureAssembliesOf(services, featureAssemblies);
            if (assemblies is [])
            {
                throw new ArgumentException(
                    "An application without Modules names the Feature assemblies whose commands and queries need a tenant.",
                    nameof(featureAssemblies)
                );
            }

            ComposedFeatures.In(services).TenantScoped.Add(IModuleServices.ModuleOf(services));
            foreach (var operation in OperationTypes.Dispatchable(ImplementingTypes.Of(typeof(object), assemblies)))
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Scoped(
                        typeof(IAuthorizer<>).MakeGenericType(operation),
                        typeof(TenantRequirement<>).MakeGenericType(operation)
                    )
                );
            }

            return services;
        }
    }
}
