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
        /// container, or in this Module when called on a Module's builder. It adds the generic repository over it, and the
        /// <see cref="IUnitOfWorkConnection"/> through which a plain SQL write joins the active Unit of Work's transaction.
        /// A database package such as <c>PouryaKit.Persistence.Postgres</c> calls it; Feature code never does.
        /// </summary>
        public IServiceCollection AddPouryaKitEfCore<TDbContext>()
            where TDbContext : PouryaKitDbContext
        {
            ArgumentNullException.ThrowIfNull(services);
            // Added, not tried: on a Module's builder it becomes that Module's own, whatever the Host already holds.
            services.AddScoped<IUnitOfWork>(provider => new EfUnitOfWork(
                provider.GetRequiredService<TDbContext>(),
                provider.GetRequiredService<Operation>(),
                provider.GetRequiredService<DomainEventDrain>()
            ));
            services.TryAddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));
            services.TryAddScoped<IUnitOfWorkConnection, EfUnitOfWorkConnection>();
            return services;
        }
    }
}
