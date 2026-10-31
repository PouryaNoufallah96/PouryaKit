using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Composes the store-neutral half of the Messaging Capability's durable message store and Outbox on Wolverine, on a
/// Host's application builder, for a store package's registration.
/// </summary>
public static class MessageStoreHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Runs the Host's deliveries on Wolverine with a durable message store over the connection string named
        /// <paramref name="connectionName"/>, which <paramref name="persistMessages"/> gives Wolverine with that string:
        /// <see cref="PouryaKitMessageStoreOptions"/>, checked when the Host starts, set its durability mode and inbox
        /// stale time; the Integration Events an Operation publishes and the commands it schedules wait in its scope for
        /// its Unit of Work, which writes them with <see cref="OutboxWriter"/>. Throws when the configuration has no such
        /// connection string or leaves it blank, and when a store package's registration already declared the store: a
        /// Host declares one. Everything <c>UsePouryaKitWolverine()</c> does applies too, and
        /// <paramref name="configure"/> adds Host-specific Wolverine configuration, as it does there. For a store
        /// package's registration, such as <c>UsePouryaKitWolverine(connectionName)</c> of
        /// <c>PouryaKit.Messaging.Wolverine.EFCore</c>, which also registers its Outbox and Receipts; a Host calls that one.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitMessageStore(
            string connectionName,
            Action<WolverineOptions, string> persistMessages,
            Action<WolverineOptions>? configure = null
        )
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            ArgumentNullException.ThrowIfNull(persistMessages);

            // A blank one too: Wolverine takes one of spaces, and the Host is built and fails only when it starts,
            // naming no connection.
            var connectionString = builder.Configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"The configuration has no connection string named {connectionName} for the message store, or leaves it blank. Set ConnectionStrings:{connectionName}."
                );
            }

            // Wolverine's UseWolverine runs once per Host, so a second store package's registration would fail on it.
            if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(OutboxWriter)))
            {
                throw new InvalidOperationException(
                    "The Host's message store is already declared by a store package's registration, such as UsePouryaKitMarten of PouryaKit.Messaging.Wolverine.Marten, which declares it when it comes first. Declare it once: in a Host of EF Core and Stream Modules, call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore before UsePouryaKitMarten, which then joins that store."
                );
            }

            builder
                .Services.AddOptions<PouryaKitMessageStoreOptions>()
                .Validate(
                    store => store.InboxStaleTime > TimeSpan.Zero,
                    "PouryaKitMessageStoreOptions.InboxStaleTime is zero or negative; set it longer than zero."
                )
                .ValidateOnStart();
            builder.Services.AddSingleton<IWolverineExtension, MessageStoreExtension>();
            builder.Services.TryAddScoped<PublishedIntegrationEvents>();
            builder.Services.TryAddScoped<IIntegrationEventOutbox>(scope =>
                scope.GetRequiredService<PublishedIntegrationEvents>()
            );
            builder.Services.TryAddScoped<ScheduledDelayedCommands>();
            builder.Services.TryAddScoped<IDelayedCommandOutbox>(scope =>
                scope.GetRequiredService<ScheduledDelayedCommands>()
            );
            builder.Services.TryAddScoped(scope => new OutboxWriter(
                scope.GetRequiredService<IWolverineRuntime>(),
                scope,
                scope.GetRequiredService<PublishedIntegrationEvents>(),
                scope.GetRequiredService<ScheduledDelayedCommands>()
            ));
            return builder.UsePouryaKitWolverine(wolverine =>
            {
                persistMessages(wolverine, connectionString);
                configure?.Invoke(wolverine);
            });
        }
    }
}
