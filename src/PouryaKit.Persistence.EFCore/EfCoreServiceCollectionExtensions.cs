using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

public static class EfCoreServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes <typeparamref name="TDbContext"/>, already registered, the Unit of Work of the commands that run in this
        /// container, or in this Module when called on a Module's builder, and adds the generic repository over it.
        /// A database package such as <c>PouryaKit.Persistence.Postgres</c> calls it; Feature code never does.
        /// </summary>
        public IServiceCollection AddPouryaKitEfCore<TDbContext>()
            where TDbContext : PouryaKitDbContext
        {
            ArgumentNullException.ThrowIfNull(services);
            services.TryAddScoped<IUnitOfWork>(provider => new EfUnitOfWork(
                provider.GetRequiredService<TDbContext>(),
                provider.GetRequiredService<Operation>()
            ));
            services.TryAddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));
            return services;
        }
    }
}
