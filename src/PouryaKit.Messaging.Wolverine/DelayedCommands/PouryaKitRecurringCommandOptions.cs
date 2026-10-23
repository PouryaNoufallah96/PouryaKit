namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// The cadence of one recurring command, named by its declaration's name, lower-cased: configure it through the builder
/// <c>AddPouryaKitRecurringCommand</c> returns, for example with <c>BindConfiguration(section)</c>.
/// </summary>
public sealed class PouryaKitRecurringCommandOptions
{
    /// <summary>
    /// The longest <see cref="Every"/>, 36500 days: each occurrence is due at most that long after now, which stays far
    /// from <see cref="DateTimeOffset.MaxValue"/>.
    /// </summary>
    public static readonly TimeSpan MaxEvery = TimeSpan.FromDays(36500);

    /// <summary>
    /// The time between two occurrences; required, longer than zero and at most <see cref="MaxEvery"/>. A change applies
    /// from the next occurrence a running occurrence or a starting Host stores.
    /// </summary>
    public TimeSpan Every { get; set; }
}
