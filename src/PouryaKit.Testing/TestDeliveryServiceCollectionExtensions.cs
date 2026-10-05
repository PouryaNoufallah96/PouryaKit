using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using Wolverine;

namespace PouryaKit.Testing;

public static class TestDeliveryServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Runs the Host's deliveries at test speed: Solo durability, so a stopped Host's deliveries are recovered at
        /// once; two retries of a failed delivery, after <see cref="RetryDelay"/> each; and the message store polled for
        /// retries and recoverable deliveries every <see cref="PollingTime"/>. Call it after <c>UsePouryaKitWolverine</c>.
        /// </summary>
        public IServiceCollection UsePouryaKitTestDelivery()
        {
            ArgumentNullException.ThrowIfNull(services);
            services.Configure<PouryaKitMessageStoreOptions>(store => store.Durability = DurabilityMode.Solo);
            services.Configure<PouryaKitMessagingOptions>(messaging => messaging.RetryDelays = [RetryDelay, RetryDelay]);
            services.ConfigureWolverine(wolverine =>
            {
                wolverine.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
                wolverine.Durability.ScheduledJobPollingTime = PollingTime;
            });
            return services;
        }
    }

    public static TimeSpan RetryDelay { get; } = TimeSpan.FromMilliseconds(100);

    public static TimeSpan PollingTime { get; } = TimeSpan.FromMilliseconds(100);
}
