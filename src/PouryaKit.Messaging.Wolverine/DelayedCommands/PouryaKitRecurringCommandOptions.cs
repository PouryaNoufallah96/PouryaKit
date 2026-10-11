namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// The cadence of one recurring command, named by its declaration's name, lower-cased: configure it through the builder
/// <c>AddPouryaKitRecurringCommand</c> returns, for example with <c>BindConfiguration(section)</c>.
/// </summary>
public sealed class PouryaKitRecurringCommandOptions
{
    /// <summary>
    /// The time between two occurrences; required, and longer than zero. A change applies from the next occurrence a
    /// running occurrence or a starting Host stores.
    /// </summary>
    public TimeSpan Every { get; set; }
}
