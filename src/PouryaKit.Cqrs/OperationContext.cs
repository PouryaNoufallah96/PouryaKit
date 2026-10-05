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
    /// Operation starts a new chain of work, and its correlation is its own <see cref="OperationId"/>.
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

    /// <summary>The Operation, or message, that caused this one; null for the Operation that started the chain.</summary>
    public Guid? CausationId { get; }

    /// <summary>
    /// The context of a new Operation this one causes: the same Actor, tenant and correlation, caused by this Operation.
    /// It is a trusted continuation and is not authorized again (G.6).
    /// </summary>
    public OperationContext Continue() => new(Actor, Tenant, CorrelationId, OperationId);
}
