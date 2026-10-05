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
        /// Runs the Host's deliveries on Wolverine, with no database: every Notification Handler and Integration Event
        /// Handler a Composition Root subscribed gets its own durable local queue and is delivered, retried and
        /// acknowledged on its own, in a scope begun as a continuation of the Operation that published the message, in
        /// the subscribing Module. A failed Result or an exception is retried after
        /// <see cref="PouryaKitMessagingOptions.RetryDelays"/>. A stored delivery whose type nothing subscribes to any
        /// more is set aside as a dead letter, never treated as handled. Wolverine discovers no handlers of its own.
        /// The Host fails to start when a subscription's Integration Event is published by no Module of the Host, or
        /// when Modules publish Integration Events and nothing registered an Outbox for them, as the overload of
        /// <c>PouryaKit.Messaging.Wolverine.EFCore</c> does. <paramref name="configure"/> adds Host-specific Wolverine
        /// configuration; a Host with a database calls that overload.
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
