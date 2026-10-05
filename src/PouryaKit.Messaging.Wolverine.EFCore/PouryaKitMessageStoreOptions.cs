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
    /// Notification committed but never sent while its Host lives on. Set it longer than the longest delivery the Host
    /// expects, queue wait included; a delivery still running when it passes is handed out again, and runs again
    /// concurrently, on every recovery cycle until one run finishes.
    /// </summary>
    public TimeSpan InboxStaleTime { get; set; } = TimeSpan.FromMinutes(5);
}
