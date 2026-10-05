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
