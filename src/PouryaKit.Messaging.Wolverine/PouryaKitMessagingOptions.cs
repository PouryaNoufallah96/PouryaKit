namespace PouryaKit.Messaging.Wolverine;

/// <summary>The Kit's delivery settings for one Host; change them with <c>services.Configure</c>.</summary>
public sealed class PouryaKitMessagingOptions
{
    /// <summary>
    /// How long a failed delivery waits before each further attempt: one attempt more than there are delays, after which
    /// the delivery is set aside as a dead letter. Each wait is stored with the delivery, not held in memory, so a
    /// restart neither loses nor repeats it. With a message store's inbox stale time, keep the delays together, plus
    /// every attempt's run, shorter than it: an attempt that runs after it is handed out again while it runs. That rule
    /// covers a delivery sent at its commit; a delayed command or a recurring occurrence due later than the stale time
    /// runs stale from its first attempt (<c>PouryaKitMessageStoreOptions.InboxStaleTime</c>). Null, or a negative delay,
    /// stops the Host when it starts, naming this option. An empty schedule fails the start too, in Wolverine's own retry
    /// policy and with Wolverine's message, not this option's: a failed delivery is always retried at least once.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)];
}
