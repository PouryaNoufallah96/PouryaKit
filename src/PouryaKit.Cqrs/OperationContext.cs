namespace PouryaKit.Cqrs;

/// <summary>
/// The facts about who and what caused one Operation, available wherever the Operation runs: inject it into a handler,
/// an authorizer or a validator. It is plain data, set by whoever opens the Operation's scope (a Module's Gateway, the
/// HTTP boundary, a message delivery) and never read from an ambient request. Create one for each Operation; work an
/// Operation causes runs with <see cref="Continue"/>.
/// </summary>
public sealed record OperationContext
{
    /// <summary>
    /// The context of a new Operation run by <paramref name="actor"/>. With no <paramref name="correlationId"/> the
    /// Operation starts a new chain of work, and its correlation is its own <see cref="OperationId"/>. A
    /// <paramref name="causationId"/> is recorded as given, but the context is never a trusted continuation.
    /// </summary>
    public OperationContext(Actor actor, string? tenant = null, Guid? correlationId = null, Guid? causationId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        OperationId = Guid.CreateVersion7();
        Actor = actor;
        Tenant = tenant;
        CorrelationId = correlationId ?? OperationId;
        CausationId = causationId;
    }

    /// <summary>The identity of the Operation that runs with this context.</summary>
    public Guid OperationId { get; }

    public Actor Actor { get; }

    /// <summary>The tenant or company the Operation runs for; null where the application has none.</summary>
    public string? Tenant { get; }

    /// <summary>Shared by every Operation in one chain of work, from the Operation that started it.</summary>
    public Guid CorrelationId { get; }

    /// <summary>
    /// The Operation, or message, that caused this one; null for the Operation that started the chain. It is
    /// informational: <see cref="IsTrustedContinuation"/>, not a causation, marks work that runs on another
    /// Operation's authority.
    /// </summary>
    public Guid? CausationId { get; }

    /// <summary>
    /// True only for a context the Kit restored from material it wrote itself, such as a message delivery restored
    /// from the headers its Outbox wrote when the causing Operation committed. Such work runs on the authority of the
    /// Operation that caused it (G.6), so a permission check does not authorize its Actor again; an authorizer of the
    /// application still runs, with the original Actor, and recognises it by this flag. Nothing outside the Kit sets
    /// it: neither the constructor nor <see cref="Continue"/> does.
    /// </summary>
    public bool IsTrustedContinuation { get; private init; }

    /// <summary>
    /// The context of a new Operation this one causes: the same Actor, tenant and correlation, caused by this Operation.
    /// It is not a trusted continuation, even inside a running Operation: work continued in process is authorized as
    /// a nested command is, against the Actor's own permissions.
    /// </summary>
    public OperationContext Continue() => new(Actor, Tenant, CorrelationId, OperationId);

    // The one way to establish trust, for Kit code that restores a context from material the Kit wrote: delivery in
    // PouryaKit.Messaging.Wolverine, and the trusted-peer binding of PouryaKit.Web, the two assemblies this one's
    // InternalsVisibleTo names. No public entry point could be callable by them and not by an application.
    internal static OperationContext RestoreTrusted(Actor actor, string? tenant, Guid correlationId, Guid causationId) =>
        new(actor, tenant, correlationId, causationId) { IsTrustedContinuation = true };
}
