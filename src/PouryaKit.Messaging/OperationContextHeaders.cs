namespace PouryaKit.Messaging;

/// <summary>
/// The message headers that carry the Operation Context of the Operation that published a message, so the work it
/// causes runs as a continuation of that Operation: the same Actor, tenant and correlation, caused by it. Every
/// transport writes and reads these names.
/// </summary>
public static class OperationContextHeaders
{
    public const string ActorId = "pouryakit-actor-id";

    /// <summary><c>Person</c> or <c>System</c>.</summary>
    public const string ActorKind = "pouryakit-actor-kind";

    /// <summary>Absent where the application has no tenant.</summary>
    public const string Tenant = "pouryakit-tenant";

    public const string CorrelationId = "pouryakit-correlation-id";

    /// <summary>The <c>OperationId</c> of the Operation that published the message.</summary>
    public const string CausationId = "pouryakit-causation-id";

    /// <summary>
    /// The W3C <c>tracestate</c> of the activity current when the Operation published the message; absent when it had
    /// none. The transport carries the <c>traceparent</c> itself (ADR 0009).
    /// </summary>
    public const string TraceState = "pouryakit-tracestate";
}
