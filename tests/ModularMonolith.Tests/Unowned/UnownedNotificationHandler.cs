using PouryaKit.Domain;
using PouryaKit.Messaging;

namespace ModularMonolith.Tests.Unowned;

public sealed record UnownedDomainEvent : DomainEvent;

/// <summary>The one Notification Handler of its assembly, which no Module's Composition Root declares.</summary>
public sealed class UnownedNotificationHandler : INotificationHandler<UnownedDomainEvent>
{
    public Task<Result> Handle(UnownedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
