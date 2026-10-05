using Wolverine;

namespace PouryaKit.Messaging.Wolverine.EFCore;

/// <summary>The message store settings of one Host; change them with <c>services.Configure</c>.</summary>
public sealed class PouryaKitMessageStoreOptions
{
    /// <summary>
    /// <see cref="DurabilityMode.Balanced"/>, the default, is safe with any number of Host instances on one store.
    /// <see cref="DurabilityMode.Solo"/> assumes a single instance and recovers a stopped instance's deliveries at once.
    /// </summary>
    public DurabilityMode Durability { get; set; } = DurabilityMode.Balanced;

    /// <summary>
    /// How long a delivery may stay with an instance that took it before the store hands it out again: the remedy for a
    /// Notification committed but never sent while its Host lives on. It counts from when the delivery was stored, not
    /// from its latest attempt, so set it longer than every retry delay together plus every attempt's run, queue wait
    /// included (<c>PouryaKitMessagingOptions.RetryDelays</c>). A delivery still running when it passes is handed out
    /// again, concurrently, on every recovery cycle until one run finishes; its Receipt keeps the effect to one, while
    /// each copy waits on it with a connection of its own.
    /// </summary>
    public TimeSpan InboxStaleTime { get; set; } = TimeSpan.FromMinutes(5);
}
