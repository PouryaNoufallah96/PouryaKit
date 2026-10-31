using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// One handler subscribed to one message type: a Notification Handler to a Domain Event, or an Integration Event Handler
/// to an Integration Event. Its durable <see cref="Name"/> is its queue and half of its Receipts' key, and
/// <see cref="Module"/> is the Module its deliveries run in, null in an application without Modules. The name is kept
/// lower-cased, as its local queue is, so a change of case only is the same subscription everywhere, Receipts included.
/// </summary>
internal sealed class Subscription
{
    private readonly Invoker _invoker;

    private Subscription(string name, Type message, Type handler, Type? module, Type invoker)
    {
        Name = (name ?? "").ToLowerInvariant();
        Message = message;
        Handler = handler;
        Module = module;
        // The handler and its message may be internal: they are only ever named through reflection, never in generated
        // code, which would need them public.
        _invoker = (Invoker)Activator.CreateInstance(invoker.MakeGenericType(message))!;
    }

    public string Name { get; }

    public Type Message { get; }

    public Type Handler { get; }

    public Type? Module { get; }

    /// <summary>True for a Notification Handler's subscription, whose message is a Domain Event.</summary>
    public bool IsNotification => Message.IsAssignableTo(typeof(DomainEvent));

    public static Subscription OfNotificationHandler(
        string name,
        Type domainEvent,
        Type handler,
        Type? module
    ) => new(name, domainEvent, handler, module, typeof(NotificationInvoker<>));

    public static Subscription OfIntegrationEventHandler(
        string name,
        Type integrationEvent,
        Type handler,
        Type? module
    ) => new(name, integrationEvent, handler, module, typeof(IntegrationEventInvoker<>));

    public Task<Result> InvokeAsync(
        IServiceProvider scope,
        object message,
        CancellationToken cancellationToken
    ) => _invoker.InvokeAsync(scope.GetRequiredService(Handler), message, cancellationToken);

    /// <summary>The exception a delivery throws when the handler returned a failed Result, so it is retried.</summary>
    public Exception Failed(ImmutableArray<Error> errors) => _invoker.Failed(Name, errors);

    private abstract class Invoker
    {
        public abstract Task<Result> InvokeAsync(
            object handler,
            object message,
            CancellationToken cancellationToken
        );

        public abstract Exception Failed(string subscription, ImmutableArray<Error> errors);
    }

    private sealed class NotificationInvoker<TDomainEvent> : Invoker
        where TDomainEvent : DomainEvent
    {
        public override Task<Result> InvokeAsync(
            object handler,
            object message,
            CancellationToken cancellationToken
        ) =>
            ((INotificationHandler<TDomainEvent>)handler).Handle(
                (TDomainEvent)message,
                cancellationToken
            );

        public override Exception Failed(string subscription, ImmutableArray<Error> errors) =>
            new NotificationHandlerFailedException(subscription, errors);
    }

    private sealed class IntegrationEventInvoker<TIntegrationEvent> : Invoker
        where TIntegrationEvent : class
    {
        public override Task<Result> InvokeAsync(
            object handler,
            object message,
            CancellationToken cancellationToken
        ) =>
            ((IIntegrationEventHandler<TIntegrationEvent>)handler).Handle(
                (TIntegrationEvent)message,
                cancellationToken
            );

        public override Exception Failed(string subscription, ImmutableArray<Error> errors) =>
            new IntegrationEventHandlerFailedException(subscription, errors);
    }
}
