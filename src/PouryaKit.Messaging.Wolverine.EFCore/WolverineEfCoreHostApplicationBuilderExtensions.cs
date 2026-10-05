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
        /// writes the Domain Events its Operation handled, those with a Notification Handler, and the Integration Events
        /// its Notification Handler published, those with an Integration Event Handler, to its Outbox in the same commit
        /// as the change, one durable delivery per subscription, and sends them only after the commit. Each handler's
        /// delivery records a Receipt in its Module's Unit of Work, so a redelivered or concurrently duplicated
        /// Notification or Integration Event applies its effect once. The store is the database of the connection string named <paramref name="connectionName"/>,
        /// which every Module's DbContext must also use, in the schema <paramref name="schema"/>; Wolverine creates its
        /// tables there when the Host starts. <see cref="PouryaKitMessageStoreOptions"/> holds the durability mode and
        /// the inbox stale time. Everything <c>UsePouryaKitWolverine()</c> of <c>PouryaKit.Messaging.Wolverine</c> does
        /// applies too.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitWolverine(string connectionName, string schema = "wolverine")
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
            builder.Services.TryAddScoped<PublishedIntegrationEvents>();
            builder.Services.TryAddScoped<IIntegrationEventOutbox>(scope => scope.GetRequiredService<PublishedIntegrationEvents>());
            builder.Services.TryAddScoped<IReceipts, EfReceipts>();
            return builder.UsePouryaKitWolverine(wolverine =>
            {
                wolverine.PersistMessagesWithPostgresql(connectionString, schema);
                // Eager is the mode in which Wolverine's own EF Core middleware commits a handler's effect and its
                // inbox mark together; the Kit's Unit of Work owns its transaction either way.
                wolverine.UseEntityFrameworkCoreTransactions(TransactionMiddlewareMode.Eager);
            });
        }
    }
}
