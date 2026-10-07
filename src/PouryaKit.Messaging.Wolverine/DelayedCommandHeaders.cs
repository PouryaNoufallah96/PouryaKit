namespace PouryaKit.Messaging.Wolverine;

/// <summary>The message header the Outbox writes on every delayed command, recurring ones included.</summary>
public static class DelayedCommandHeaders
{
    /// <summary>
    /// The time the command was scheduled for, in UTC, round-trip format: for a recurring command, the occurrence it
    /// runs, which keys its Receipt. The message store clears its own copy when the command comes due.
    /// </summary>
    public const string DueAt = "pouryakit-delayed-command-due-at";
}
