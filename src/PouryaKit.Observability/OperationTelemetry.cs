namespace PouryaKit.Observability;

/// <summary>
/// The names the Kit's Operation telemetry is emitted under, for the Host's exporter or a test's listener. Each command
/// and query the dispatcher runs is one activity <see cref="ActivityName"/> and one <see cref="DurationName"/>
/// measurement, both from <see cref="SourceName"/> and tagged with the Operation's Module, the command or query, its
/// Actor kind and its outcome.
/// </summary>
public static class OperationTelemetry
{
    /// <summary>The name of the Kit's <c>ActivitySource</c> and of its <c>Meter</c>.</summary>
    public const string SourceName = "PouryaKit";

    /// <summary>The activity of one command or query; one nested in another is its child.</summary>
    public const string ActivityName = "pouryakit.operation";

    /// <summary>A histogram of each command's or query's duration, in seconds, with fractions.</summary>
    public const string DurationName = "pouryakit.operation.duration";

    /// <summary>The Module the Operation runs in, by its type's name; absent in an application without Modules.</summary>
    public const string ModuleTag = "pouryakit.module";

    /// <summary>The command's or query's type name.</summary>
    public const string OperationTag = "pouryakit.operation";

    /// <summary><c>Person</c> or <c>System</c>; absent when the scope's Operation has not begun.</summary>
    public const string ActorKindTag = "pouryakit.actor.kind";

    /// <summary><see cref="Succeeded"/>, <see cref="Failed"/> or <see cref="Threw"/>.</summary>
    public const string OutcomeTag = "pouryakit.outcome";

    /// <summary>The outcome of a command or query that returned a successful Result.</summary>
    public const string Succeeded = "success";

    /// <summary>The outcome of a command or query that returned a failed Result.</summary>
    public const string Failed = "failure";

    /// <summary>The outcome of a command or query that threw.</summary>
    public const string Threw = "exception";
}
