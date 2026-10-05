using PouryaKit.Domain;
using PouryaKit.Messaging;

namespace ModularMonolith.Tests.Misnamed;

/// <summary>Marks the assembly whose Notification Handlers are misnamed.</summary>
public static class MisnamedNotifications;

// A wire name declared empty.
[WireName("")]
internal sealed record UnnamedDomainEvent : DomainEvent;

internal sealed class UnnamedNotificationHandler : INotificationHandler<UnnamedDomainEvent>
{
    public Task<Result> Handle(UnnamedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed record BlankedDomainEvent : DomainEvent;

[SubscriptionName(" ")]
internal sealed class BlankNotificationHandler : INotificationHandler<BlankedDomainEvent>
{
    public Task<Result> Handle(BlankedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed record SpacedDomainEvent : DomainEvent;

[SubscriptionName("misnamed.has space")]
internal sealed class SpacedNotificationHandler : INotificationHandler<SpacedDomainEvent>
{
    public Task<Result> Handle(SpacedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

// Two subscriptions whose names differ only in case, so they would share one lower-cased queue.
internal sealed record TwinnedDomainEvent : DomainEvent;

[SubscriptionName("misnamed.Twin")]
internal sealed class FirstTwinNotificationHandler : INotificationHandler<TwinnedDomainEvent>
{
    public Task<Result> Handle(TwinnedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

[SubscriptionName("misnamed.twin")]
internal sealed class SecondTwinNotificationHandler : INotificationHandler<TwinnedDomainEvent>
{
    public Task<Result> Handle(TwinnedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

// A subscription named after a queue Wolverine keeps in memory for its own agents.
internal sealed record ReservedDomainEvent : DomainEvent;

[SubscriptionName("Agents")]
internal sealed class ReservedNotificationHandler : INotificationHandler<ReservedDomainEvent>
{
    public Task<Result> Handle(ReservedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

// A wire name declared, but not valid.
[WireName("misnamed.bad name")]
internal sealed record BadlyNamedDomainEvent : DomainEvent;

internal sealed class BadlyNamedNotificationHandler : INotificationHandler<BadlyNamedDomainEvent>
{
    public Task<Result> Handle(BadlyNamedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

// Two Domain Events whose derived wire names are the same: the DomainEvent suffix is left out.
internal sealed record ShelvedDomainEvent : DomainEvent;

internal sealed record Shelved : DomainEvent;

internal sealed class ShelveNotificationHandler : INotificationHandler<ShelvedDomainEvent>
{
    public Task<Result> Handle(ShelvedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}

internal sealed class ShelveAgainNotificationHandler : INotificationHandler<Shelved>
{
    public Task<Result> Handle(Shelved domainEvent, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success());
}
