using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using JasperFx.MultiTenancy;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;
using Wolverine.Marten;

namespace M2.Marten;

/// <summary>The connection string Marten's store was given, by its name in the Host's configuration.</summary>
public sealed record MartenConnection(string Name, string ConnectionString);

/// <summary>How the Spike composes the Stream write model: the Host's store, a Stream Module, and the per-Module stores.</summary>
public static class MartenRegistration
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// The Host's one Marten store over the connection string <paramref name="connectionName"/>, events and documents in
        /// the schema <c>events</c>, the Kit's JSON, conjoined tenancy, integrated with Wolverine's message store in the
        /// schema <paramref name="messageStoreSchema"/>. With <paramref name="checkDatabase"/>, the Host fails when it is
        /// built if the store's database is not the message store's (<paramref name="messageStoreConnectionName"/>).
        /// </summary>
        public IHostApplicationBuilder AddSpikeMarten(
            string connectionName,
            string messageStoreConnectionName,
            string messageStoreSchema = "wolverine",
            bool checkDatabase = true,
            Action<StoreOptions>? configure = null
        )
        {
            var connectionString =
                builder.Configuration.GetConnectionString(connectionName)
                ?? throw new InvalidOperationException($"No connection string {connectionName}.");
            builder.Services.AddSingleton(new MartenConnection(connectionName, connectionString));
            builder
                .Services.AddMarten(options =>
                {
                    options.Connection(connectionString);
                    options.DatabaseSchemaName = "events";
                    options.UseSystemTextJsonForSerialization(configure: json => json.UsePouryaKitJson());
                    options.Events.TenancyStyle = TenancyStyle.Conjoined;
                    options.Policies.AllDocumentsAreMultiTenanted();
                    configure?.Invoke(options);
                })
                .ApplyAllDatabaseChangesOnStartup()
                .IntegrateWithWolverine(integration => integration.MessageStorageSchemaName = messageStoreSchema);
            builder.Services.AddHostedService<MartenIdempotencyCleanup>();
            if (checkDatabase)
            {
                builder.Services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<ICompositionCheck>(
                        new MartenStoreDatabaseCheck(
                            messageStoreConnectionName,
                            builder.Configuration.GetConnectionString(messageStoreConnectionName) ?? ""
                        )
                    )
                );
            }

            return builder;
        }
    }

    extension(ModuleBuilder module)
    {
        /// <summary>
        /// Makes the Module a Stream Module: its Unit of Work is a Marten one, and its Receipts, Snapshot versions and
        /// idempotency keys are kept on that Unit of Work's session, in tables of its own <paramref name="schema"/>, keyed by
        /// the Module. With <paramref name="documentReceipts"/>, Receipts are Marten documents instead.
        /// </summary>
        public ModuleBuilder UseSpikeMartenStreams(string schema, bool documentReceipts = false)
        {
            module.TryAddScoped<StoreNeutralOutbox>();
            module.TryAddScoped<MartenUnitOfWork>();
            module.AddScoped<IUnitOfWork>(services => services.GetRequiredService<MartenUnitOfWork>());
            module.TryAddScoped<IStreams>(services => services.GetRequiredService<MartenUnitOfWork>());
            module.AddKeyedScoped<IReceipts>(
                module.Module,
                (services, _) => documentReceipts
                    ? new DocumentReceipts(services.GetRequiredService<MartenUnitOfWork>())
                    : new MartenReceipts(services.GetRequiredService<MartenUnitOfWork>(), schema)
            );
            module.AddKeyedScoped<IIdempotencyStore>(
                module.Module,
                (services, _) => new MartenIdempotencyStore(services.GetRequiredService<MartenUnitOfWork>(), services.GetRequiredService<TimeProvider>(), schema)
            );
            module.AddSingleton(new MartenModuleSchema(schema));
            module.ConfigureMarten(options =>
            {
                foreach (var table in MartenTables.Of(schema))
                {
                    options.Storage.ExtendedSchemaObjects.Add(table);
                }

                options.Schema.For<ReceiptDocument>().SingleTenanted();
            });
            return module;
        }

        /// <summary>
        /// Keys the Kit's EF Receipts and EF idempotency store by this EF Module, as the EF packages would on a Module's
        /// builder. Their types are internal to the Kit, so they are named here.
        /// </summary>
        public ModuleBuilder UseSpikeEfStores()
        {
            var receipts = typeof(WolverineEfCoreHostApplicationBuilderExtensions).Assembly.GetType("PouryaKit.Messaging.Wolverine.EFCore.EfReceipts", throwOnError: true)!;
            var keys = typeof(PouryaKitIdempotencyOptions).Assembly.GetType("PouryaKit.Persistence.EFCore.EfIdempotencyStore", throwOnError: true)!;
            module.Add(new ServiceDescriptor(typeof(IReceipts), module.Module, receipts, ServiceLifetime.Scoped));
            module.Add(new ServiceDescriptor(typeof(IIdempotencyStore), module.Module, keys, ServiceLifetime.Scoped));
            return module;
        }
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Replaces the unkeyed Receipts and idempotency store with resolvers that take the Operation's Module's keyed
        /// one. Replaced, never tried: before the Kit's registrations, their TryAdd finds the resolver; after, the resolver
        /// replaces them; a second call replaces the first.
        /// </summary>
        public IServiceCollection UseSpikeModuleStoreRouters()
        {
            services.Replace(ServiceDescriptor.Scoped(Route<IReceipts>));
            services.Replace(ServiceDescriptor.Scoped(Route<IIdempotencyStore>));
            return services;
        }
    }

    private static T Route<T>(IServiceProvider services)
        where T : notnull
    {
        var module =
            services.GetRequiredService<Operation>().Module
            ?? throw new InvalidOperationException($"{typeof(T).Name} was needed in a scope whose Operation names no Module.");
        return services.GetKeyedService<T>(module) ?? throw new InvalidOperationException($"{module.Name} keeps no {typeof(T).Name}.");
    }
}
