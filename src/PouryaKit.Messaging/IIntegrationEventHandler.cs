using PouryaKit.Domain;

namespace PouryaKit.Messaging;

/// <summary>
/// A Module's reaction to one <typeparamref name="TIntegrationEvent"/> another Module published: the business effect
/// only. It runs in a scope of its own in the receiving Module, as a continuation of the Operation that published the
/// Integration Event, and its effect commits together with its Receipt, so a redelivered or concurrently duplicated
/// Integration Event applies it once. It returns a Result and never saves, commits or acknowledges; a failed Result,
/// like an exception, is retried. It may be <c>internal</c>. A Module subscribes it in its Composition Root.
/// </summary>
public interface IIntegrationEventHandler<TIntegrationEvent>
    where TIntegrationEvent : class
{
    /// <summary>Applies the business effect of <paramref name="integrationEvent"/> and returns its Result.</summary>
    Task<Result> Handle(TIntegrationEvent integrationEvent, CancellationToken cancellationToken);
}
