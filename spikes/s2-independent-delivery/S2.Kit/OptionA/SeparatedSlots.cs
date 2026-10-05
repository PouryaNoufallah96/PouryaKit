using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace S2.Kit.OptionA;

/// <summary>
/// Option A with key types: the same separated, sticky queues, but each subscription's Wolverine handler type is closed
/// over a Kit-owned public key type (<see cref="Slot"/>, <see cref="Slot{T}"/>, <c>Slot&lt;Slot&lt;Slot&gt;&gt;</c> …)
/// instead of the handler type, so generated code never names the handler. The message type still appears in it.
/// </summary>
public sealed class SeparatedSlots : IDeliveryOption
{
    public void Configure(KitMessaging messaging, IServiceCollection services, WolverineOptions opts)
    {
        opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
        var names = new SlotNames();
        services.AddSingleton(names);
        var slot = typeof(Slot);
        foreach (var s in messaging.Subscriptions)
        {
            var adapter = typeof(SlotHandler<,>).MakeGenericType(s.MessageType, slot);
            names[slot] = s.Name;
            slot = typeof(Slot<>).MakeGenericType(slot);
            opts.Discovery.IncludeType(adapter);
            opts.LocalQueue(s.Name).AddStickyHandler(adapter);
            if (messaging.For(s.MessageType).Count() == 1) opts.PublishMessage(s.MessageType).ToLocalQueue(s.Name);
        }
    }

    public ValueTask PublishAsync(IMessageBus outbox, object message) => outbox.PublishAsync(message);
}

public sealed class Slot;

public sealed class Slot<T>;

public sealed class SlotNames : Dictionary<Type, string>;

public sealed class SlotHandler<TMessage, TSlot>(KitMessaging messaging, SlotNames names, IServiceScopeFactory scopes)
{
    public Task Handle(TMessage message, CancellationToken ct) =>
        messaging.Named(names[typeof(TSlot)]).DeliverAsync(scopes, message!, ct);
}
