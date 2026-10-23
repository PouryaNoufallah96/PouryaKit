using Wolverine;

namespace PouryaKit.Messaging.Wolverine.Sagas;

/// <summary>
/// The base of a Saga's timeout message: a step returns one to schedule it <paramref name="Within"/> from now, on the
/// saga's own queue, and the saga's step that takes it runs then, unless the saga completed first. The Kit keeps an
/// identity of its own for each timeout a step schedules, in the saga's state (ADR 0013). Declare it with
/// <c>Timeout&lt;TTimeout&gt;()</c> on the saga's declaration; it names no saga identity member of its own.
/// </summary>
/// <param name="Within">How long after the step that schedules it the timeout falls due.</param>
public abstract record SagaTimeout(TimeSpan Within) : TimeoutMessage(Within);
