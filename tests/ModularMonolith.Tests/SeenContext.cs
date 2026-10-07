using PouryaKit.Cqrs;

namespace ModularMonolith.Tests;

/// <summary>An <see cref="OperationContext"/> as an endpoint answers it.</summary>
internal sealed record SeenContext(Guid OperationId, SeenActor Actor, string? Tenant, Guid CorrelationId, Guid? CausationId);

internal sealed record SeenActor(string Id, ActorKind Kind);
