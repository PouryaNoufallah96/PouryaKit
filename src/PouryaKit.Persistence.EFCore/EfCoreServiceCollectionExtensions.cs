using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// Composes the Persistence and the Unit of Work Capability over EF Core on a Module's or a Host's services: the DbContext as the Unit of
/// Work, its repositories, and the Errors of its unique constraints.
/// </summary>
public static class EfCoreServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes <typeparamref name="TDbContext"/>, already registered, the Unit of Work of the commands that run in this
        /// container, or in this Module when called on a Module's builder. It adds the generic repository over it, the
        /// <see cref="IUnitOfWorkConnection"/> through which a plain SQL write joins the active Unit of Work's transaction,
        /// and the <see cref="IIdempotencyStore"/> that keeps the keys of <c>[Idempotent]</c> commands in that
        /// transaction, in the Module's <see cref="IdempotencyTable"/>. Once for the Host, in every Host, it adds the
        /// background service that removes expired keys, as <see cref="PouryaKitIdempotencyOptions"/> sets, and checks
        /// those options when the Host starts: at each cleanup it walks every DbContext this call made a Unit of Work,
        /// probes each one's schema with PostgreSQL's <c>to_regclass</c>, and returns silently for one with no key
        /// table, the DbContext of a Module with no <c>[Idempotent]</c> command. The store, its cleanup and the table
        /// are PostgreSQL-only, as the Kit is: <c>on conflict</c>, <c>ctid</c>, <c>to_regclass</c>, PostgreSQL's
        /// SQLSTATE codes and a <c>timestamp with time zone</c> column.
        /// When a messaging package registered an <see cref="IUnitOfWorkOutbox"/>, the Unit of Work commits through it; a
        /// Host with an Outbox (<see cref="IOutbox"/>) but no <see cref="IUnitOfWorkOutbox"/>, as one whose only message
        /// store is <c>UsePouryaKitMarten</c>'s, fails when it is built, naming its EF Core Modules, or the application when no Module has one.
        /// It records the DbContext, once, as a <see cref="DbContextConnection"/> with <paramref name="connectionName"/>,
        /// the connection string it stores over when its registration names one, so the Kit's composition checks refuse,
        /// when the Host's container is built, a named connection string the Host's configuration lacks or leaves blank,
        /// and compare its connection with the message store's. A name replaces an earlier record without one; the same name
        /// again, ignoring case as configuration keys do, keeps the first spelling; a second, different name for the same
        /// DbContext throws. On a Module's builder it also records the DbContext, and registers the
        /// <see cref="IIdempotencyStore"/>, keyed by the Module, as its Unit of Work is keyed, so a Kit check reads the model
        /// of the Module's own Unit of Work and an Operation of the Module keeps its key in the Module's own store, whatever
        /// else the Host holds (<see cref="IModuleServices.RouteByModule{TService}"/>).
        /// A database package such as <c>PouryaKit.Persistence.Postgres</c> calls it; Feature code never does.
        /// </summary>
        public IServiceCollection AddPouryaKitEfCore<TDbContext>(string? connectionName = null)
            where TDbContext : PouryaKitDbContext
        {
            ArgumentNullException.ThrowIfNull(services);
            // An Outbox writes on this DbContext's connection, whoever registered the DbContext.
            var recorded = services.FirstOrDefault(descriptor =>
                descriptor.ImplementationInstance is DbContextConnection connection && connection.DbContext == typeof(TDbContext)
            );
            if (recorded is null)
            {
                services.AddSingleton(new DbContextConnection(connectionName, typeof(TDbContext)));
            }
            else if (connectionName is not null && recorded.ImplementationInstance is DbContextConnection { Name: var recordedName })
            {
                if (recordedName is null)
                {
                    services[services.IndexOf(recorded)] = ServiceDescriptor.Singleton(new DbContextConnection(connectionName, typeof(TDbContext)));
                }
                // Configuration keys ignore case, so "app" and "APP" name one connection string.
                else if (!string.Equals(recordedName, connectionName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{typeof(TDbContext).Name} is already stored over the connection string {recordedName}, so it cannot also be stored over {connectionName}. Give one DbContext one connection string."
                    );
                }
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, DbContextConnectionsCheck>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, UnitOfWorkOutboxCheck>());
            var module = IModuleServices.ModuleOf(services);
            if (module is not null)
            {
                services.AddKeyedSingleton<DbContextConnection>(module, new DbContextConnection(connectionName, typeof(TDbContext)));
            }

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
            if (module is not null)
            {
                services.TryAddKeyedScoped<IIdempotencyStore, EfIdempotencyStore>(module);
            }
            else
            {
                services.TryAddScoped<IIdempotencyStore, EfIdempotencyStore>();
            }

            IModuleServices.RouteByModule<IIdempotencyStore>(services);
            services.TryAddSingleton(TimeProvider.System);
            services.AddHostedService<IdempotencyKeyCleanup>();
            services.AddPouryaKitIdempotencyOptions();
            return services;
        }

        /// <summary>
        /// Makes a violation of the unique constraint or unique index named <paramref name="constraint"/>, in the tables
        /// of <typeparamref name="TDbContext"/>, fail the Operation with <paramref name="error"/>: the Unit of Work rolls
        /// back and returns it as a failed Result. A Module calls it in its Composition Root, once per constraint; any
        /// other database error stays an exception. It covers a constraint the database checks when the row is written;
        /// a violation of one declared <c>DEFERRABLE INITIALLY DEFERRED</c> surfaces when the transaction commits, as an
        /// exception. Throws when the constraint is already mapped for that DbContext.
        /// </summary>
        public IServiceCollection MapPouryaKitUniqueConstraint<TDbContext>(string constraint, Error error)
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
