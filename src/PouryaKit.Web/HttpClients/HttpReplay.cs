namespace PouryaKit.Web;

/// <summary>Which requests a client's resilience handler may send again after a transient failure.</summary>
public enum HttpReplay
{
    /// <summary>
    /// Only GET, HEAD, OPTIONS and TRACE, the methods that change nothing. Every other method, POST, PUT, PATCH, DELETE
    /// and an extension method such as PROPPATCH alike, is sent once: a write that failed in transit may still have taken
    /// effect.
    /// </summary>
    SafeMethods,

    /// <summary>
    /// Every request, writes included. Choose it only for a client whose downstream API makes every write safe to
    /// replay, such as one that accepts an idempotency key the request carries.
    /// </summary>
    AllRequests,
}
