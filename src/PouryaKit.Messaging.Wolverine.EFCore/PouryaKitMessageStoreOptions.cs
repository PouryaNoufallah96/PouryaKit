using Wolverine;

namespace PouryaKit.Messaging.Wolverine.EFCore;

/// <summary>The message store settings of one Host; change them with <c>services.Configure</c>.</summary>
public sealed class PouryaKitMessageStoreOptions
{
    /// <summary>
    /// <see cref="DurabilityMode.Balanced"/>, the default, is safe with any number of Host instances on one store when
    /// all are instances of one Host: another Host, one with other handlers or Modules, takes a store of its own,
    /// another database or another schema through the <c>schema</c> argument of <c>UsePouryaKitWolverine</c>, since on a
    /// shared schema a node without the other Host's handlers recovers its deliveries and dead-letters them.
    /// <see cref="DurabilityMode.Solo"/> assumes a single instance and recovers a stopped instance's deliveries at once.
    /// </summary>
    public DurabilityMode Durability { get; set; } = DurabilityMode.Balanced;

    /// <summary>
    /// How long a delivery may stay with an instance that took it before the store hands it out again: the remedy for a
    /// Notification committed but never sent while its Host lives on. It counts from when the delivery was stored, not
    /// from its latest attempt: longer than every retry delay together plus every attempt's run, queue wait included
    /// (<c>PouryaKitMessagingOptions.RetryDelays</c>), it keeps a delivery sent at its commit from going stale, but a
    /// delayed command or a recurring occurrence due later than it, as an hourly one always is, runs stale from its
    /// first attempt and, held by no instance while it waits, is handed out again only when a recovery poll finds an
    /// attempt of it still running. A delivery still running when it passes is handed out again, concurrently, on every
    /// recovery cycle until one run finishes; its Receipt keeps the effect to one, and a recurring command's next
    /// occurrence to one, while each copy waits on it with a connection of its own. Zero or less stops the Host when it
    /// starts, naming this option.
    /// </summary>
    public TimeSpan InboxStaleTime { get; set; } = TimeSpan.FromMinutes(5);
}
