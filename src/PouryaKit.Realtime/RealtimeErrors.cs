using PouryaKit.Domain;

namespace PouryaKit.Realtime;

/// <summary>The Errors a realtime push and an SSE endpoint mapped with <c>MapPouryaKitRealtime</c> fail with.</summary>
public static class RealtimeErrors
{
    /// <summary>
    /// The push's notification, its Channel, tenant and event in the Kit's JSON, is 8000 bytes or more, which PostgreSQL
    /// does not carry. Nothing was pushed.
    /// </summary>
    public static readonly Error PayloadTooLarge = Error.Validation(
        "Realtime.PayloadTooLarge",
        "The realtime event, with its channel and tenant, is 8000 bytes or more, which PostgreSQL does not carry. Push a small fact or a reference, and let the client read the rest through a query."
    );

    /// <summary>The request names no Channel to listen on. No connection was opened.</summary>
    public static readonly Error ChannelRequired = Error.Validation(
        "Realtime.ChannelRequired",
        "Name at least one channel to listen on, with the channel query parameter."
    );

    /// <summary>The endpoint's rule does not admit the caller to a Channel the request names. No connection was opened.</summary>
    public static readonly Error ChannelNotAdmitted = Error.Forbidden(
        "Realtime.ChannelNotAdmitted",
        "The caller may not listen on a channel the request names."
    );
}
