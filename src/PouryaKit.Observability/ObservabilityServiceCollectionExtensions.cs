using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Observability;

/// <summary>
/// Composes the Observability Capability on a Host's services: an activity and a duration measurement for every command
/// and query the dispatcher runs.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Emits an activity and a duration measurement for every command and query the dispatcher runs, under the
        /// names <see cref="OperationTelemetry"/> lists. The activity is current for the whole Operation, its Unit of
        /// Work and Outbox write included, so the work the Operation publishes joins its trace; a delivery's Operation
        /// continues the publishing Operation's trace. Nothing is exported: the Host adds its exporter for
        /// <see cref="OperationTelemetry.SourceName"/>. Call it after <c>AddPouryaKitModules</c> or
        /// <c>AddPouryaKitCqrs</c>, which register the dispatcher it wraps; throws when none is registered. A second call
        /// adds nothing.
        /// </summary>
        public IServiceCollection AddPouryaKitObservability()
        {
            ArgumentNullException.ThrowIfNull(services);
            var registered = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IDispatcher) && !descriptor.IsKeyedService);
            if (registered?.ImplementationFactory?.Target is ObservedDispatcherFactory)
            {
                return services;
            }

            if (registered?.ImplementationType is not { } dispatcher)
            {
                throw new InvalidOperationException(
                    "No dispatcher of the Kit is registered to observe. Call AddPouryaKitObservability after AddPouryaKitModules or AddPouryaKitCqrs."
                );
            }

            services.Remove(registered);
            services.AddScoped<IDispatcher>(new ObservedDispatcherFactory(ActivatorUtilities.CreateFactory(dispatcher, [])).Create);
            return services;
        }
    }

    private sealed class ObservedDispatcherFactory(ObjectFactory dispatcher)
    {
        public IDispatcher Create(IServiceProvider scope) =>
            new ObservedDispatcher((IDispatcher)dispatcher(scope, null), scope.GetRequiredService<Operation>());
    }
}
