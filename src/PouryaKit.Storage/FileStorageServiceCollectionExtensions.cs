using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;

namespace PouryaKit.Storage;

/// <summary>Registers a file storage provider as the Host's one <see cref="IFileStorage"/>.</summary>
public static class FileStorageServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes the provider <paramref name="factory"/> builds, when first resolved, the Host's one singleton
        /// <see cref="IFileStorage"/>, as each provider package's registration does. A Host composes exactly one: its
        /// build fails, through the Kit's composition checks, naming each registration's type, or the factory that builds
        /// it, when it registers another <see cref="IFileStorage"/>, before or after this call, a second call included,
        /// and naming the contract when it removes every one.
        /// </summary>
        public IServiceCollection AddPouryaKitFileStorage(
            Func<IServiceProvider, IFileStorage> factory
        )
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(factory);
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICompositionCheck>(
                    new OneProviderCheck<IFileStorage>("AddPouryaKitFileStorage", "file storage")
                )
            );
            services.AddSingleton(factory);
            return services;
        }
    }
}
