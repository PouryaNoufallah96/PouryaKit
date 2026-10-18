namespace SagaSpike.Kit;

/// <summary>What a step's deduplication claim is keyed by.</summary>
public enum SagaClaimKey
{
    /// <summary>The Kit's key: the saga's name, the input's wire name and the event's identity.</summary>
    EventIdentity,

    /// <summary>A red run's key: the saga's name and the event's identity, without the input's wire name.</summary>
    WithoutWireName,

    /// <summary>The rejected option: the saga's name and the delivery's envelope id alone.</summary>
    EnvelopeId,

    /// <summary>No claim: Wolverine's saga storage alone.</summary>
    None,
}

/// <summary>
/// The Spike's switches, one Host each: the Kit's mechanisms as #142 would ship them are the defaults, and each switch is a
/// rejected option a test tries or a probe a test reads. A package would have none of them.
/// </summary>
public sealed class SagaSpikeOptions
{
    /// <summary>Whether the Kit's route delivers an Integration Event to a saga as its <see cref="SagaInput{TSaga,TEvent}"/>.</summary>
    public bool KitRoute { get; set; } = true;

    /// <summary>Whether a saga's storage is opened by the Kit, which claims each step in the saga's transaction.</summary>
    public bool KitStorage { get; set; } = true;

    /// <summary>What the claim is keyed by.</summary>
    public SagaClaimKey ClaimKey { get; set; } = SagaClaimKey.EventIdentity;

    /// <summary>Whether a completed saga's claims are deleted with its state, rather than kept.</summary>
    public bool DeleteClaimsWithState { get; set; }

    /// <summary>Whether a saga's identity includes the tenant.</summary>
    public bool TenantInIdentity { get; set; } = true;

    /// <summary>Whether an emission goes through the Kit's adapter, or is published as Wolverine publishes from a saga.</summary>
    public bool KitEmissions { get; set; } = true;

    /// <summary>Whether the saga's state is written through the message store's own settings, in its schema.</summary>
    public bool StoreSettings { get; set; } = true;

    /// <summary>A probe run after each step's commit and before the step returns, given the input's wire name and identity.</summary>
    public Func<string, Guid, Task>? AfterCommit { get; set; }
}
