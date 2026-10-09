namespace PouryaKit.Cqrs;

/// <summary>
/// The success a committed Operation recorded for a key: its Result's value in the Kit's JSON representation, or null
/// for a command without a value.
/// </summary>
public sealed record RecordedSuccess(string? Response);
