using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PouryaKit.Persistence.Postgres;

/// <summary>Composes the number sequences of the Persistence and the Unit of Work Capability on a Module's services.</summary>
public static class NumberSequenceServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds <see cref="INumberSequences"/>, through which a handler takes the next gap-free number of a scope key in
        /// the Operation's Unit of Work, the Module's own in a modular Host. Call it on the builder of a Module stored
        /// with <c>UsePouryaKitPostgres</c>, and create the Module's table with a migration that calls
        /// <c>CreateNumberSequenceTable</c>; without that table the first number throws, naming it.
        /// </summary>
        public IServiceCollection AddPouryaKitNumberSequences()
        {
            ArgumentNullException.ThrowIfNull(services);
            services.TryAddScoped<INumberSequences, PostgresNumberSequences>();
            return services;
        }
    }
}
