using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Marten;

/// <summary>Composes the Stream write model's Unit of Work on a Module's or a Host's services.</summary>
public static class StreamServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes the commands that run in this container, or in this Module when called on a Module's builder, run in a
        /// Marten Unit of Work over the Host's one Marten store (<c>AddPouryaKitMarten</c>): their Feature code reads and
        /// starts Streams through <see cref="IStreams"/>, and the Unit of Work appends what each Stream raised, with
        /// <c>FetchForWriting</c> at the version it read, and commits it with what the Operation published. The Unit of Work
        /// owns its connection and transaction from before the command to the commit, so the keys of <c>[Idempotent]</c>
        /// commands, which <see cref="IIdempotencyStore"/> keeps in the table <c>pouryakit_idempotency_keys</c> of
        /// <paramref name="schema"/>, the Module's own, commit with the Streams' events or not at all, a key-only Operation
        /// included. Marten creates that table with the store when the Host starts. On a Module's builder the Unit of Work
        /// and the store are the Module's own, keyed by it (<see cref="IModuleServices.RouteByModule{TService}"/>), and the
        /// Module is recorded among the Host's <see cref="StreamModules"/>, whose Receipts a messaging package keeps in the
        /// same schema. A Host with an Outbox (<see cref="IOutbox"/>) but none for the Marten Unit of Work
        /// (<see cref="IStreamOutbox"/>), as when the EF Core message store sits beside <c>AddPouryaKitMarten</c>, fails
        /// when it is built, naming its Stream Modules. Feature code never calls it.
        /// </summary>
        public IServiceCollection AddPouryaKitStreams(string schema)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);
            var module = IModuleServices.ModuleOf(services);

            // Added, not tried: on a Module's builder it becomes that Module's own, whatever the Host already holds.
            services.AddScoped<IUnitOfWork>(provider => new MartenUnitOfWork(
                provider.GetRequiredService<IDocumentStore>(),
                provider.GetRequiredService<Operation>(),
                provider.GetRequiredService<DomainEventDrain>(),
                provider.GetRequiredService<OperationContext>,
                provider.GetService<IStreamOutbox>(),
                provider.GetRequiredService<ILogger<MartenUnitOfWork>>(),
                schema
            ));
            services.TryAddScoped<IStreams>(provider =>
                provider.GetRequiredService<IUnitOfWork>() as IStreams
                ?? throw new InvalidOperationException(
                    "Streams are read and started in a Stream Module's Marten Unit of Work, but this Operation's Unit of Work is another write model's. Call AddPouryaKitStreams in the Module's Composition Root."
                )
            );
            if (module is not null)
            {
                services.TryAddKeyedScoped<IIdempotencyStore>(
                    module,
                    (provider, _) => NewKeys(provider)
                );
            }
            else
            {
                services.TryAddScoped<IIdempotencyStore>(NewKeys);
            }

            IModuleServices.RouteByModule<IIdempotencyStore>(services);
            services.ConfigureMarten(options =>
                options.Storage.ExtendedSchemaObjects.Add(IdempotencyKeyTable.In(schema))
            );
            StreamModules.In(services).Add(module, schema);
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICompositionCheck, StreamOutboxCheck>()
            );
            return services;
        }
    }

    private static MartenIdempotencyStore NewKeys(IServiceProvider provider) =>
        new(
            provider.GetRequiredService<IUnitOfWork>(),
            provider.GetRequiredService<TimeProvider>()
        );
}
