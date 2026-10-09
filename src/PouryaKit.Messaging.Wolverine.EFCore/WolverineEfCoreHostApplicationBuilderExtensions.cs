using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;
using Wolverine;
using Wolverine.Postgresql;

namespace PouryaKit.Messaging.Wolverine.EFCore;

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
        /// a Snapshot older than one already applied not at all. The store is the database of the connection string named <paramref name="connectionName"/>,
        /// in the schema <paramref name="schema"/>, where Wolverine creates its tables when the Host starts; each Outbox
        /// is written in its Unit of Work's own transaction, so the Host fails when it is built if a Module's DbContext
        /// names another database, and this call throws when the configuration has no such connection string or leaves
        /// it blank. <see cref="PouryaKitMessageStoreOptions"/> holds the durability mode and
        /// the inbox stale time. Everything <c>UsePouryaKitWolverine()</c> of <c>PouryaKit.Messaging.Wolverine</c> does
        /// applies too; <paramref name="configure"/> adds Host-specific Wolverine configuration, as it does there.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitWolverine(string connectionName, string schema = "wolverine", Action<WolverineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);

            // A blank one too: Wolverine takes one of spaces, and the Host is built and fails only when it starts,
            // naming no connection.
            var connectionString = builder.Configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"The configuration has no connection string named {connectionName} for the message store, or leaves it blank. Set ConnectionStrings:{connectionName}."
                );
            }

            builder.Services.AddOptions<PouryaKitMessageStoreOptions>();
            builder.Services.AddSingleton<IWolverineExtension, MessageStoreExtension>();
            builder.Services.TryAddScoped<IUnitOfWorkOutbox, WolverineOutbox>();
            builder.Services.TryAddScoped<IOutbox, WolverineOutbox>();
            builder.Services.TryAddScoped<PublishedIntegrationEvents>();
            builder.Services.TryAddScoped<IIntegrationEventOutbox>(scope => scope.GetRequiredService<PublishedIntegrationEvents>());
            builder.Services.TryAddScoped<ScheduledDelayedCommands>();
            builder.Services.TryAddScoped<IDelayedCommandOutbox>(scope => scope.GetRequiredService<ScheduledDelayedCommands>());
            builder.Services.TryAddScoped<IReceipts, EfReceipts>();
            builder.Services.TryAddEnumerable(
                ServiceDescriptor.Singleton<ICompositionCheck>(new MessageStoreDatabaseCheck(connectionName, connectionString))
            );
            return builder.UsePouryaKitWolverine(wolverine =>
            {
                wolverine.PersistMessagesWithPostgresql(connectionString, schema);
                configure?.Invoke(wolverine);
            });
        }
    }
}
