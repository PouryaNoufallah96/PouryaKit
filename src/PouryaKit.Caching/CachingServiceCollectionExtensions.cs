using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;

namespace PouryaKit.Caching;

public static class CachingServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Fills the dispatcher's query cache slot (ADR 0002) with <c>HybridCache</c>: a <see cref="ICacheableQuery"/> run
        /// outside a write Unit of Work is answered from it, and the tags an <see cref="IInvalidatesCache"/> command
        /// declares are evicted once its Operation commits. Registers <c>HybridCache</c> unless one is registered. It
        /// keeps entries in this Host's memory, and also in the Host's <c>IDistributedCache</c> when the Host registers
        /// one, such as Redis: another Host then reads an entry this one stored. Evicting a tag is certain only in the
        /// Host whose Operation committed; another Host may return its own copies until they expire. A second call adds
        /// nothing.
        /// </summary>
        public IServiceCollection AddPouryaKitCaching()
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddHybridCache();
            services.TryAddSingleton<IQueryCache, HybridQueryCache>();
            return services;
        }
    }
}
