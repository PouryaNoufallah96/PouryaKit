using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Persistence.EFCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
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
        /// which every Module's DbContext must also use, in the schema <paramref name="schema"/>; Wolverine creates its
        /// tables there when the Host starts. <see cref="PouryaKitMessageStoreOptions"/> holds the durability mode and
        /// the inbox stale time. Everything <c>UsePouryaKitWolverine()</c> of <c>PouryaKit.Messaging.Wolverine</c> does
        /// applies too; <paramref name="configure"/> adds Host-specific Wolverine configuration, as it does there.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitWolverine(string connectionName, string schema = "wolverine", Action<WolverineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            ArgumentException.ThrowIfNullOrWhiteSpace(schema);

            var connectionString =
                builder.Configuration.GetConnectionString(connectionName)
                ?? throw new InvalidOperationException(
                    $"The configuration has no connection string named {connectionName} for the message store."
                );
            builder.Services.AddOptions<PouryaKitMessageStoreOptions>();
            builder.Services.AddSingleton<IWolverineExtension, MessageStoreExtension>();
            builder.Services.TryAddScoped<IUnitOfWorkOutbox, WolverineOutbox>();
            builder.Services.TryAddScoped<INotificationOutbox, WolverineOutbox>();
            builder.Services.TryAddScoped<PublishedIntegrationEvents>();
            builder.Services.TryAddScoped<IIntegrationEventOutbox>(scope => scope.GetRequiredService<PublishedIntegrationEvents>());
            builder.Services.TryAddScoped<ScheduledDelayedCommands>();
            builder.Services.TryAddScoped<IDelayedCommandOutbox>(scope => scope.GetRequiredService<ScheduledDelayedCommands>());
            builder.Services.TryAddScoped<IReceipts, EfReceipts>();
            return builder.UsePouryaKitWolverine(wolverine =>
            {
                wolverine.PersistMessagesWithPostgresql(connectionString, schema);
                // Eager is the mode in which Wolverine's own EF Core middleware commits a handler's effect and its
                // inbox mark together; the Kit's Unit of Work owns its transaction either way.
                wolverine.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
                configure?.Invoke(wolverine);
            });
        }
    }
}
