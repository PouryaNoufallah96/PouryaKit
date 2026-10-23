using Wolverine;

namespace Offending.Saga;

/// <summary>A saga of Wolverine's own: its steps would run with none of the Kit's route, claim, tenant guard or adapter.</summary>
public sealed class OwnSaga : Wolverine.Saga
{
    /// <summary>The saga's identity, which Wolverine reads from the message itself.</summary>
    public string Id { get; set; } = "";
}

/// <summary>A timeout of Wolverine's own, outside the Kit's base type.</summary>
public sealed record OwnTimeout(TimeSpan Within) : TimeoutMessage(Within);
