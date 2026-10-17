namespace PouryaKit.Realtime;

/// <summary>
/// The server-sent event types the Kit writes itself, beside each pushed event, whose type is its Channel. Neither
/// carries data, and no Channel takes either name.
/// </summary>
public static class RealtimeEvents
{
    /// <summary>The first event of every connection: from here on, every event pushed for its Channels reaches it.</summary>
    public const string Connected = "connected";

    /// <summary>
    /// Sent to every connected client once the instance's listener has reconnected after losing its connection: events
    /// pushed meanwhile are lost, so the client reads again, through a query, the state it shows.
    /// </summary>
    public const string Gap = "gap";
}
