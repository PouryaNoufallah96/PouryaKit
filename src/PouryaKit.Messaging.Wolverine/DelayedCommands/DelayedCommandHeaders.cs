namespace PouryaKit.Messaging.Wolverine;

/// <summary>The message header that tells a stored recurring occurrence from a delayed command.</summary>
public static class DelayedCommandHeaders
{
    /// <summary>
    /// The time of the recurring command's occurrence the message runs, in UTC, round-trip format, which keys its
    /// Receipt. The Outbox writes it on every occurrence of a recurring command and on nothing else: a delayed command
    /// never carries it. A delivery whose header does not match its declaration, an occurrence on a queue now declared
    /// for a delayed command or a delayed command on one now declared recurring, is set aside as a dead letter and
    /// never run under the other's authority.
    /// </summary>
    public const string Occurrence = "pouryakit-recurring-occurrence";
}
