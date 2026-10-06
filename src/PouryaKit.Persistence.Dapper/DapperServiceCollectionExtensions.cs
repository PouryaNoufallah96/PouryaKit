using System.Reflection;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Core;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.Dapper;

public static class DapperServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds Dapper reads over the connection string named <paramref name="connectionName"/> in the Host's
        /// configuration, and makes every typed ID in <paramref name="typedIdAssemblies"/> usable as a Dapper parameter
        /// and result. Call it from each Module's Composition Root that reads with Dapper, usually with the Module's own
        /// assembly. The Host holds one <see cref="IReadConnectionFactory"/> and one connection pool, shared by every
        /// Module that names the same connection; naming a different connection in the same Host throws here.
        /// </summary>
        public IServiceCollection AddPouryaKitDapper(string connectionName, params Assembly[] typedIdAssemblies)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            ArgumentNullException.ThrowIfNull(typedIdAssemblies);

            AddTypedIdHandlers(typedIdAssemblies);

            var registered = services
                .Select(descriptor => descriptor.ImplementationInstance)
                .OfType<ReadConnection>()
                .FirstOrDefault();
            if (registered is not null)
            {
                return registered.Name == connectionName
                    ? services
                    : throw new InvalidOperationException(
                        $"Dapper reads already use the connection '{registered.Name}' in this Host, so they cannot also use '{connectionName}'."
                    );
            }

            services.AddSingleton(new ReadConnection(connectionName));
            services.AddSingleton<IReadConnectionFactory>(provider => new ReadConnectionFactory(
                provider.GetRequiredService<IConfiguration>().GetConnectionString(connectionName)
                    ?? throw new InvalidOperationException(
                        $"Dapper reads need the connection string '{connectionName}' in the Host's ConnectionStrings configuration."
                    )
            ));
            return services;
        }
    }

    // Dapper's type handlers are process-wide, and Dapper serialises their registration. A handler depends only on its
    // typed ID's type, so when a second Module, or a second Host in the same process, registers the same typed ID again,
    // the handler it replaces behaves identically and every Host keeps the same mapping.
    private static void AddTypedIdHandlers(Assembly[] assemblies)
    {
        foreach (var type in ImplementingTypes.Of(typeof(TypedId<>), assemblies))
        {
            if (TypedId.GetValueType(type) is { } valueType)
            {
                var handler = Activator.CreateInstance(typeof(TypedIdTypeHandler<,>).MakeGenericType(type, valueType))!;
                SqlMapper.AddTypeHandler(type, (SqlMapper.ITypeHandler)handler);
            }
        }
    }

    // Marks the connection the Host's Dapper reads use.
    private sealed record ReadConnection(string Name);
}
