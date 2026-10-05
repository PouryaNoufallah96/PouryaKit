using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine;

public static class WolverineHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Sets up the Host's routing, delivery and retry on Wolverine, but no Outbox: every Notification Handler and
        /// Integration Event Handler a Composition Root subscribed gets its own durable local queue and is delivered,
        /// retried and acknowledged on its own, in a scope begun as a continuation of the Operation that published the
        /// message, in the subscribing Module. A failed Result or an exception is retried after
        /// <see cref="PouryaKitMessagingOptions.RetryDelays"/>, then that subscription's delivery alone is set aside as a
        /// dead letter in Wolverine's dead-letter storage, received at <c>local://{subscription}/</c>, to inspect and
        /// replay there. A stored delivery whose type or subscription no longer exists is set aside the same way. None of
        /// them is ever treated as handled. Wolverine discovers no handlers of its own.
        /// Nothing this overload registers writes an Outbox (<see cref="INotificationOutbox"/>), so a Host that declares
        /// Notification Handlers, Integration Event Handlers or published Integration Events calls the overload of
        /// <c>PouryaKit.Messaging.Wolverine.EFCore</c> instead, or fails when it starts; so does a Host whose
        /// subscription's Integration Event no Module publishes. <paramref name="configure"/> adds Host-specific
        /// Wolverine configuration.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitWolverine(Action<WolverineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            WolverineMessageNaming.InsertFirst<WireNaming>();
            Subscriptions.In(builder.Services);
            builder.Services.AddOptions<PouryaKitMessagingOptions>();
            builder.Services.TryAddScoped<CommandHandler<Deliver>, DeliverHandler>();
            builder.Services.TryAddScoped<IntegrationEventPublisher>();
            builder.Services.TryAddScoped<IIntegrationEventPublisher>(scope => scope.GetRequiredService<IntegrationEventPublisher>());
            builder.Services.AddSingleton<IWolverineExtension, PouryaKitWolverineExtension>();
            builder.UseWolverine(wolverine =>
            {
                wolverine.Discovery.DisableConventionalDiscovery();
                wolverine.UnknownMessageBehavior = UnknownMessageBehavior.DeadLetterQueue;
                wolverine.Policies.UseDurableLocalQueues();
                wolverine.UseSystemTextJsonForSerialization(json => json.Converters.Add(new TypedIdJsonConverterFactory()));
                configure?.Invoke(wolverine);
            });
            return builder;
        }
    }
}
