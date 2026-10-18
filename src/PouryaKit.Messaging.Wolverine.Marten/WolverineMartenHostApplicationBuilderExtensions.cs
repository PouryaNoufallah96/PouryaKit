using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Marten;
using Wolverine.Marten;
using Wolverine.Postgresql;

namespace PouryaKit.Messaging.Wolverine.Marten;

/// <summary>
/// Composes the Messaging Capability for the Stream write model, Wolverine's Marten outbox on the Host's one message store,
/// on a Host's application builder.
/// </summary>
public static class WolverineMartenHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Registers the Host's one Marten store over the connection string named <paramref name="connectionName"/>, as
        /// <c>AddPouryaKitMarten</c> does, integrated with Wolverine on the Host's one message store and durability agent,
        /// in the schema <paramref name="schema"/>: every Stream Module's Unit of Work then writes the Domain Events its
        /// Operation handled, those with a Notification Handler, and the Integration Events its Notification Handler
        /// published, into the message store in the transaction of its Streams' events, and sends them after the commit; a
        /// send that fails there is logged and recovered from the store, never the Operation's failure. Each delivery into
        /// a Stream Module, Snapshots and delayed commands included, records its Receipt in the Module's own schema on that
        /// transaction (<see cref="IModuleServices.RouteByModule{TService}"/>), whichever of this call and the Modules'
        /// registrations comes first. In a Host whose Modules are all Stream Modules the message store is this call's,
        /// over the same connection string, and everything <c>UsePouryaKitMessageStore</c> does applies too. In a Host that
        /// also has EF Core Modules it is the one <c>UsePouryaKitWolverine(connectionName)</c> of
        /// <c>PouryaKit.Messaging.Wolverine.EFCore</c> declared, which comes first, and the Host fails when it is built if
        /// the Marten store is on another database than that store's; in another schema than
        /// <paramref name="schema"/>, it fails when it starts, as Wolverine finds two main stores. Throws when the
        /// configuration has no such connection string or leaves it blank, and when the Host already has its Marten store.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitMarten(string connectionName, string schema = "wolverine")
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);

            // A store package's registration has declared the store-neutral half when it registered its writer.
            if (!builder.Services.Any(descriptor => descriptor.ServiceType == typeof(OutboxWriter)))
            {
                builder.UsePouryaKitMessageStore(
                    connectionName,
                    (wolverine, connectionString) => wolverine.PersistMessagesWithPostgresql(connectionString, schema)
                );
            }

            builder.AddPouryaKitMarten(connectionName).IntegrateWithWolverine(integration => integration.MessageStorageSchemaName = schema);
            builder.Services.TryAddScoped<MartenStreamOutbox>();
            builder.Services.TryAddScoped<IStreamOutbox>(scope => scope.GetRequiredService<MartenStreamOutbox>());
            builder.Services.TryAddScoped<IOutbox>(scope => scope.GetRequiredService<MartenStreamOutbox>());
            StreamModules
                .In(builder.Services)
                .ForEach(
                    (module, moduleSchema) =>
                    {
                        if (module is null)
                        {
                            builder.Services.TryAddScoped<IReceipts>(scope => new MartenReceipts(scope.GetRequiredService<IUnitOfWork>()));
                        }
                        else
                        {
                            builder.Services.AddKeyedScoped<IReceipts>(module, (scope, _) => new MartenReceipts(scope.GetRequiredService<IUnitOfWork>()));
                        }

                        builder.Services.ConfigureMarten(options =>
                        {
                            foreach (var table in ReceiptTables.In(moduleSchema))
                            {
                                options.Storage.ExtendedSchemaObjects.Add(table);
                            }
                        });
                    }
                );
            IModuleServices.RouteByModule<IReceipts>(builder.Services);
            builder.Services.AddSingleton(new OutboxConnection(connectionName, "The Marten store of UsePouryaKitMarten"));
            return builder;
        }
    }
}
