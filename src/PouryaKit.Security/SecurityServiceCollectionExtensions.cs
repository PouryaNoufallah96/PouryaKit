using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;

namespace PouryaKit.Security;

public static class SecurityServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers a permission check, on the pipeline's authorization hook, for every command and query in
        /// <paramref name="featureAssemblies"/>, internal types included, that declares a
        /// <see cref="RequiresPermissionAttribute"/>; nothing else is checked. The check stops an Operation with
        /// <see cref="PermissionErrors.Denied"/> when the application's <see cref="IActorPermissions"/>, which the
        /// application registers, says its Actor lacks the permission. A continuation, an Operation whose context has a
        /// causation, is not checked again; every other authorizer registered for the command or query still runs.
        /// Throws when the attribute is on a type that is not one concrete command or query.
        /// </summary>
        public IServiceCollection AddPouryaKitPermissions(params Assembly[] featureAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(featureAssemblies);

            var declared = featureAssemblies
                .Distinct()
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => type.IsDefined(typeof(RequiresPermissionAttribute), inherit: false))
                .ToList();
            List<string> problems =
            [
                .. declared
                    .Where(type => type.IsAbstract || type.ContainsGenericParameters || !IsOperation(type))
                    .Select(type =>
                        $"{type.FullName} declares a permission but is not one concrete command or query; the dispatcher would never check it."
                    ),
            ];
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
            }

            foreach (var operation in declared)
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

    private static bool IsOperation(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (
                current == typeof(Command)
                || (
                    current.IsGenericType
                    && current.GetGenericTypeDefinition() is var definition
                    && (definition == typeof(Command<>) || definition == typeof(Query<>))
                )
            )
            {
                return true;
            }
        }

        return false;
    }
}
