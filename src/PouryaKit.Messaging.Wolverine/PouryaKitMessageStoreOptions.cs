using Wolverine;

namespace PouryaKit.Messaging.Wolverine;

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
    /// How long a delivery an instance took and no longer holds waits before the store hands it out again: the remedy for
    /// a Notification committed but never sent while its Host lives on. Its clock restarts when an instance claims the
    /// delivery, a due retry, delayed command or recurring occurrence included, so a late attempt is not stale when it
    /// starts, and three times per stale time while the instance holds it, queued or running, so a long attempt is not
    /// handed out again while each restart lands in time. The store compares the database's clock with the instance's, so
    /// keep it well above their difference and a loaded instance's delay, as the five-minute default is. A delivery
    /// handed out while an attempt of it still runs, as when its Host fails to restart its clock in time, keeps one effect
    /// through its Receipt, and a recurring command one next occurrence, while each copy waits on it with a connection of
    /// its own. Zero or less stops the Host when it starts, naming this option.
    /// </summary>
    public TimeSpan InboxStaleTime { get; set; } = TimeSpan.FromMinutes(5);
}
