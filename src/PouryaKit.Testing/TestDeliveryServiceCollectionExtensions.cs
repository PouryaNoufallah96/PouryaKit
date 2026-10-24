using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Messaging.Wolverine;
using Wolverine;

namespace PouryaKit.Testing;

/// <summary>Composes delivery at test speed, part of the Testing Capability, on a Host's services.</summary>
public static class TestDeliveryServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Runs the Host's deliveries at test speed: Solo durability, so a stopped Host's deliveries are recovered at
        /// once; a failed delivery retried after each of <see cref="RetryDelays"/>; and the message store polled for
        /// retries and recoverable deliveries every <see cref="PollingTime"/>. Call it after <c>UsePouryaKitWolverine</c>.
        /// </summary>
        public IServiceCollection UsePouryaKitTestDelivery()
        {
            ArgumentNullException.ThrowIfNull(services);
            services.Configure<PouryaKitMessageStoreOptions>(store => store.Durability = DurabilityMode.Solo);
            services.Configure<PouryaKitMessagingOptions>(messaging => messaging.RetryDelays = [.. RetryDelays]);
            services.ConfigureWolverine(wolverine =>
            {
                wolverine.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
                wolverine.Durability.ScheduledJobPollingTime = PollingTime;
            });
            return services;
        }
    }

    /// <summary>The waits before each retry of a failed delivery: a delivery is attempted once more than there are.</summary>
    public static IReadOnlyList<TimeSpan> RetryDelays { get; } = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)];

    /// <summary>How often the message store is polled for retries and recoverable deliveries.</summary>
    public static TimeSpan PollingTime { get; } = TimeSpan.FromMilliseconds(100);
}
