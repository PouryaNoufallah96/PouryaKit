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
        /// Runs the Host's deliveries on Wolverine, with no database: every Notification Handler a Composition Root
        /// declared gets its own durable local queue and is delivered, retried and acknowledged on its own, in a scope
        /// begun as a continuation of the Operation that raised the Domain Event, in the subscribing Module. A failed
        /// Result or an exception is retried after <see cref="PouryaKitMessagingOptions.RetryDelays"/>. A stored delivery
        /// whose type no Notification Handler subscribes to any more is set aside as a dead letter, never treated as
        /// handled. Wolverine discovers no handlers of its own. <paramref name="configure"/> adds Host-specific Wolverine
        /// configuration; a Host with a database calls the overload of <c>PouryaKit.Messaging.Wolverine.EFCore</c>.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitWolverine(Action<WolverineOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            WolverineMessageNaming.InsertFirst<WireNaming>();
            NotificationSubscriptions.In(builder.Services);
            builder.Services.AddOptions<PouryaKitMessagingOptions>();
            builder.Services.TryAddScoped<CommandHandler<DeliverNotification>, DeliverNotificationHandler>();
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
