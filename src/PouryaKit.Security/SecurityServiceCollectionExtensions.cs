using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Core;
using PouryaKit.Cqrs;

namespace PouryaKit.Security;

/// <summary>Composes permission checks, part of the Security Capability, on a Module's or a Host's services.</summary>
public static class SecurityServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a permission check, on the pipeline's authorization hook, for every command and query that declares a
        /// <see cref="RequiresPermissionAttribute"/> and is in <paramref name="featureAssemblies"/>, internal types
        /// included, or is handled by a handler there wherever it is declared, such as in a Module's Contracts: the set
        /// <see cref="OperationTypes.Dispatchable"/> gives, which the dispatcher runs; nothing else is checked. The check
        /// stops an Operation with <see cref="PermissionErrors.Denied"/> when the application's
        /// <see cref="IActorPermissions"/>, which the application registers, says its Actor lacks the permission. A trusted continuation, an Operation whose
        /// context the Kit marked <see cref="OperationContext.IsTrustedContinuation"/> as work an authorized Operation
        /// caused (a delivery, a delayed command's run, a trusted peer's call, or
        /// <see cref="OperationContext.Continue"/> called inside the running Operation), is not checked again. A
        /// causation alone does not make one: application code can give any. Every other authorizer registered for the
        /// command or query still runs. Named with no assembly on a Module's builder (<see cref="IModuleServices"/>), it
        /// scans the assembly of the Module's definition; named assemblies replace it. Throws when the attribute is on a
        /// type the assemblies declare, or a handler there handles, that is not one concrete command or query.
        /// </summary>
        public IServiceCollection AddPouryaKitPermissions(params Assembly[] featureAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            var assemblies = IModuleServices.FeatureAssembliesOf(services, featureAssemblies);
            var declaring = ImplementingTypes
                .Candidates(typeof(object), assemblies)
                .Concat(OperationTypes.Dispatchable(ImplementingTypes.Of(typeof(object), assemblies)))
                .Distinct()
                .Where(type => type.IsDefined(typeof(RequiresPermissionAttribute), inherit: false))
                .ToList();
            List<string> problems =
            [
                .. declaring
                    .Where(type => type.IsAbstract || type.ContainsGenericParameters || !OperationTypes.IsOperation(type))
                    .Select(type =>
                        $"{type.FullName} declares a permission but is not one concrete command or query; the dispatcher would never check it."
                    ),
            ];
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
            }

            foreach (var operation in declaring)
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Scoped(
                        typeof(IAuthorizer<>).MakeGenericType(operation),
                        typeof(PermissionAuthorizer<>).MakeGenericType(operation)
                    )
                );
            }

            return services;
        }
    }
}
