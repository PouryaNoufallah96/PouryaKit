using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;

namespace PouryaKit.Validation;

public static class ValidationServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Fills the pipeline's validation step with FluentValidation and registers every validator in
        /// <paramref name="featureAssemblies"/>, internal types included.
        /// </summary>
        public IServiceCollection AddPouryaKitValidation(params Assembly[] featureAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            services.TryAdd(ServiceDescriptor.Scoped(typeof(IValidationStep<>), typeof(FluentValidationStep<>)));

            foreach (var assembly in featureAssemblies.Distinct())
            {
                foreach (var validator in AssemblyScanner.FindValidatorsInAssembly(assembly, includeInternalTypes: true))
                {
                    services.TryAddEnumerable(ServiceDescriptor.Scoped(validator.InterfaceType, validator.ValidatorType));
                }
            }

            return services;
        }
    }
}
