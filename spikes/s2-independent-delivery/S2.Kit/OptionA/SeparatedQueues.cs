using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace S2.Kit.OptionA;

/// <summary>
/// Option A: Wolverine's separated per-handler queues. Each subscription becomes its own Wolverine handler type (a
/// closed <see cref="SeparatedHandler{THandler,TMessage}"/>), pinned as a sticky handler to a durable local queue
/// named after the subscription. Wolverine routes one published message to every sticky queue.
/// </summary>
/// <param name="separated">False builds the control: the same adapters combined into one logical handler.</param>
/// <param name="pinSingleSubscriber">
/// Wolverine separates a message type's handlers only when it has more than one, so a lone subscriber's messages go to
/// the message type's queue. True routes them to the subscription's queue instead, the queue the handler keeps once a
/// second subscriber makes Wolverine separate them.
/// </param>
public sealed class SeparatedQueues(bool separated = true, bool pinSingleSubscriber = true) : IDeliveryOption
{
    public void Configure(KitMessaging messaging, IServiceCollection services, WolverineOptions opts)
    {
        opts.MultipleHandlerBehavior = separated
            ? MultipleHandlerBehavior.Separated
            : MultipleHandlerBehavior.ClassicCombineIntoOneLogicalHandler;

        foreach (var s in messaging.Subscriptions)
        {
            services.AddScoped(s.HandlerType);
            var adapter = typeof(SeparatedHandler<,>).MakeGenericType(s.HandlerType, s.MessageType);
            opts.Discovery.IncludeType(adapter);
            if (separated) opts.LocalQueue(s.Name).AddStickyHandler(adapter);
            if (separated && pinSingleSubscriber && messaging.For(s.MessageType).Count() == 1)
                opts.PublishMessage(s.MessageType).ToLocalQueue(s.Name);
        }
    }

    public ValueTask PublishAsync(IMessageBus outbox, object message) => outbox.PublishAsync(message);
}

/// <summary>The Kit-owned Wolverine handler for one subscription: a fresh scope, the Kit handler, a failed Result thrown.</summary>
public sealed class SeparatedHandler<THandler, TMessage>(IServiceScopeFactory scopes)
    where THandler : class, IIntegrationEventHandler<TMessage>
{
    public async Task Handle(TMessage message, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<THandler>().Handle(message, ct);
        if (result.IsFailure) throw new HandlerFailedException(typeof(THandler).Name, result.Error!);
    }
}
