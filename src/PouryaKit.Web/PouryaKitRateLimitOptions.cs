namespace PouryaKit.Web;

/// <summary>
/// The Host's rate limit, a fixed window per Actor applied to every request: each authenticated caller, by the value of
/// <see cref="PouryaKitWebOptions.ActorClaim"/>, gets <see cref="PermitLimit"/> requests in each <see cref="Window"/>,
/// and every unauthenticated caller shares one partition of the same size. No header, such as <c>Host</c> or
/// <c>X-Forwarded-For</c>, chooses a partition. A request beyond the limit waits in its partition's queue of
/// <see cref="QueueLimit"/> requests, oldest first, until the next window, or answers 429 as a problem response when
/// the queue is full. The limits are counted in each Host process. Off by default.
/// </summary>
/// <remarks>
/// When enabled, the Host fails when it starts unless <see cref="PermitLimit"/> is at least 1, <see cref="Window"/> is
/// positive and <see cref="QueueLimit"/> is not negative.
/// </remarks>
public sealed class PouryaKitRateLimitOptions
{
    public bool Enabled { get; set; }

    /// <summary>The requests one partition is served in each window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>The length of a window; the permits are replenished when it ends.</summary>
    public TimeSpan Window { get; set; }

    /// <summary>The requests beyond the limit that wait for the next window; 0, the default, refuses them at once.</summary>
    public int QueueLimit { get; set; }
}
