using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;
using Wolverine;
using Wolverine.Postgresql;

namespace PouryaKit.Messaging.Wolverine.EFCore;

/// <summary>
/// Composes the Messaging Capability on Wolverine, with its durable message store and every Module's Outbox in
/// PostgreSQL, on a Host's application builder.
/// </summary>
public static class WolverineEfCoreHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Runs the Host's deliveries on Wolverine with its message store in PostgreSQL: every Module's Unit of Work then
        /// writes the Domain Events its Operation handled, those with a Notification Handler, the Integration Events
        /// its Notification Handler published, and the Snapshots of the aggregates it changed, those with an Integration
        /// Event Handler, to its Outbox in the same commit as the change, one durable delivery per subscription, and sends
        /// them only after the commit; the commands it scheduled with <see cref="IDelayedCommands"/> are written in the
        /// same commit, each stored until it is due. Each handler's delivery, and each delayed command's, records a Receipt in its Module's Unit of Work, so a
        /// redelivered or concurrently duplicated Notification, Integration Event or Snapshot applies its effect once, and
        /// a Snapshot older than one already applied not at all. In a Host of Modules, a Module whose write model's package
        /// keys Receipts of its own keeps them there, and every other Module's are these
        /// (<see cref="IModuleServices.RouteByModule{TService}"/>), whichever of this call and <c>AddPouryaKitModules</c>
        /// comes first. The store is the database of the connection string named <paramref name="connectionName"/>,
        /// in the schema <paramref name="schema"/>, where Wolverine creates its tables when the Host starts; each Outbox
        /// is written in its Unit of Work's own transaction, so the Host fails when it is built if a Module's DbContext
        /// names another database, and this call throws when the configuration has no such connection string or leaves
        /// it blank. <see cref="PouryaKitMessageStoreOptions"/> holds the durability mode and
        /// the inbox stale time. Everything <see cref="MessageStoreHostApplicationBuilderExtensions.UsePouryaKitMessageStore"/>,
        /// the store-neutral half, and <c>UsePouryaKitWolverine()</c> of <c>PouryaKit.Messaging.Wolverine</c> do applies
        /// too; <paramref name="configure"/> adds Host-specific Wolverine configuration, as it does there.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitWolverine(string connectionName, string schema = "wolverine", Action<WolverineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);

            builder.UsePouryaKitMessageStore(
                connectionName,
                (wolverine, connectionString) => wolverine.PersistMessagesWithPostgresql(connectionString, schema),
                configure
            );
            builder.Services.TryAddScoped<WolverineOutbox>();
            builder.Services.TryAddScoped<IUnitOfWorkOutbox>(scope => scope.GetRequiredService<WolverineOutbox>());
            builder.Services.TryAddScoped<IOutbox>(scope => scope.GetRequiredService<WolverineOutbox>());
            // The Receipts of an application without Modules, and of every Module that keys none of its own.
            builder.Services.TryAddScoped<IReceipts, EfReceipts>();
            builder.Services.TryAddKeyedScoped<IReceipts, EfReceipts>(KeyedService.AnyKey);
            IModuleServices.RouteByModule<IReceipts>(builder.Services);
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICompositionCheck>(
                    new MessageStoreDatabaseCheck(connectionName, builder.Configuration.GetConnectionString(connectionName)!, schema)
                )
            );
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, SnapshotKeyTenancyCheck>());
            return builder;
        }
    }
}
