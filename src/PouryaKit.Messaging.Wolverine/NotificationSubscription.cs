using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// One Notification Handler subscribed to one Domain Event: its durable <see cref="Name"/>, which is its queue and half
/// of its Receipts' key, and the Module its deliveries run in, null in an application without Modules. The name is kept
/// lower-cased, as its local queue is, so a change of case only is the same subscription everywhere, Receipts included.
/// </summary>
internal sealed class NotificationSubscription
{
    private readonly Invoker _invoker;

    public NotificationSubscription(string name, Type domainEvent, Type handler, Type? module)
    {
        Name = (name ?? "").ToLowerInvariant();
        DomainEvent = domainEvent;
        Handler = handler;
        Module = module;
        // The handler and its Domain Event may be internal: they are only ever named through reflection, never in
        // generated code, which would need them public.
        _invoker = (Invoker)Activator.CreateInstance(typeof(Invoker<>).MakeGenericType(domainEvent))!;
    }

    public string Name { get; }

    public Type DomainEvent { get; }

    public Type Handler { get; }

    public Type? Module { get; }

    public Task<Result> InvokeAsync(IServiceProvider scope, DomainEvent domainEvent, CancellationToken cancellationToken) =>
        _invoker.InvokeAsync(scope.GetRequiredService(Handler), domainEvent, cancellationToken);

    private abstract class Invoker
    {
        public abstract Task<Result> InvokeAsync(object handler, DomainEvent domainEvent, CancellationToken cancellationToken);
    }

    private sealed class Invoker<TDomainEvent> : Invoker
        where TDomainEvent : DomainEvent
    {
        public override Task<Result> InvokeAsync(
            object handler,
            DomainEvent domainEvent,
            CancellationToken cancellationToken
        ) => ((INotificationHandler<TDomainEvent>)handler).Handle((TDomainEvent)domainEvent, cancellationToken);
    }
}
