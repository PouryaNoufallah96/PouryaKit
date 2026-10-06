namespace PouryaKit.Messaging.Wolverine;

/// <summary>The Kit's delivery settings for one Host; change them with <c>services.Configure</c>.</summary>
public sealed class PouryaKitMessagingOptions
{
    /// <summary>
    /// How long a failed delivery waits before each further attempt: one attempt more than there are delays, after which
    /// the delivery is set aside as a dead letter. Each wait is stored with the delivery, not held in memory, so a
    /// restart neither loses nor repeats it. With a message store's inbox stale time, keep the delays together, plus
    /// every attempt's run, shorter than it: an attempt that runs after it is handed out again while it runs.
    /// </summary>
    public TimeSpan[] RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1)];
}
