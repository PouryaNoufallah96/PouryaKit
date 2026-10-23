using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PouryaKit.AI.Postgres;

/// <summary>Composes the AI Capability's semantic search over pgvector on a Module's services.</summary>
public static class VectorStoreServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds <see cref="IVectorStore"/>, through which a handler stores text by its embedding and a query finds the
        /// stored text nearest a question, in the Operation's tenant and Unit of Work, the Module's own in a modular Host.
        /// Call it on the builder of a Module stored with <c>UsePouryaKitPostgres</c>, in a Host that registers its
        /// embedding generator with <c>AddPouryaKitEmbeddings</c>, and create each collection with a migration of the
        /// Module that calls <c>CreateVectorCollection</c>; without that table a call throws, naming it.
        /// </summary>
        public IServiceCollection AddPouryaKitVectorStore()
        {
            ArgumentNullException.ThrowIfNull(services);
            services.TryAddScoped<IVectorStore, PostgresVectorStore>();
            return services;
        }
    }
}
