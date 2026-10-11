namespace PouryaKit.Web;

/// <summary>
/// The Host's rate limit, a fixed window per Actor applied to every request: each partition gets
/// <see cref="PermitLimit"/> requests in each <see cref="Window"/>. The partition is the Operation's Actor as the HTTP
/// boundary establishes it: the person the <see cref="PouryaKitWebOptions.ActorClaim"/> names, the Actor an endpoint's
/// <c>AllowAnonymousAs(actor)</c> names for its unauthenticated callers, and for a trusted peer's call the Actor it
/// continues. A request no Actor resolves for, such as an anonymous call to an endpoint outside every Module, shares one partition. No header but a
/// trusted peer's context headers, and never <c>Host</c> or <c>X-Forwarded-For</c>, chooses a partition. A request beyond the limit waits in its partition's queue of
/// <see cref="QueueLimit"/> requests, oldest first, until the next window, or answers 429 as a problem response when
/// the queue is full. The limits are counted in each Host process. Off by default.
/// </summary>
/// <remarks>
/// When enabled, the Host fails when it starts unless <see cref="PermitLimit"/> is at least 1, <see cref="Window"/> is
/// positive and <see cref="QueueLimit"/> is not negative.
/// </remarks>
public sealed class PouryaKitRateLimitOptions
{
    /// <summary>Whether the rate limit applies; false, the default, limits nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>The requests one partition is served in each window.</summary>
    public int PermitLimit { get; set; }

    /// <summary>The length of a window; the permits are replenished when it ends.</summary>
    public TimeSpan Window { get; set; }

    /// <summary>The requests beyond the limit that wait for the next window; 0, the default, refuses them at once.</summary>
    public int QueueLimit { get; set; }
}
