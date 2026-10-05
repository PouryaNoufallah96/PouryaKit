using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

public static class EfCoreServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes <typeparamref name="TDbContext"/>, already registered, the Unit of Work of the commands that run in this
        /// container, or in this Module when called on a Module's builder. It adds the generic repository over it, and the
        /// <see cref="IUnitOfWorkConnection"/> through which a plain SQL write joins the active Unit of Work's transaction.
        /// When a messaging package registered an <see cref="IUnitOfWorkOutbox"/>, the Unit of Work commits through it.
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
                provider.GetRequiredService<DomainEventDrain>(),
                () => provider.GetRequiredService<OperationContext>(),
                provider.GetService<IUniqueConstraintReader>(),
                provider.GetKeyedServices<UniqueConstraintError>(typeof(TDbContext)),
                provider.GetService<IUnitOfWorkOutbox>()
            ));
            services.TryAddScoped(typeof(IRepository<,>), typeof(EfRepository<,>));
            services.TryAddScoped<IUnitOfWorkConnection, EfUnitOfWorkConnection>();
            return services;
        }

        /// <summary>
        /// Makes a violation of the unique constraint or unique index named <paramref name="constraint"/>, in the tables
        /// of <typeparamref name="TDbContext"/>, fail the Operation with <paramref name="error"/>: the Unit of Work rolls
        /// back and returns it as a failed Result. A Module calls it in its Composition Root, once per constraint; any
        /// other database error stays an exception. Throws when the constraint is already mapped for that DbContext.
        /// </summary>
        public IServiceCollection MapUniqueConstraint<TDbContext>(string constraint, Error error)
            where TDbContext : PouryaKitDbContext
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(constraint);
            ArgumentNullException.ThrowIfNull(error);

            if (
                services.Any(descriptor =>
                    descriptor.IsKeyedService
                    && Equals(descriptor.ServiceKey, typeof(TDbContext))
                    && descriptor.KeyedImplementationInstance is UniqueConstraintError mapped
                    && mapped.Constraint == constraint
                )
            )
            {
                throw new InvalidOperationException(
                    $"The unique constraint {constraint} of {typeof(TDbContext).Name} is already mapped to an Error; map each constraint once."
                );
            }

            services.AddKeyedSingleton<UniqueConstraintError>(typeof(TDbContext), new UniqueConstraintError(constraint, error));
            return services;
        }
    }
}

/// <summary>The Error a Module mapped to one of its unique constraints.</summary>
internal sealed record UniqueConstraintError(string Constraint, Error Error);
