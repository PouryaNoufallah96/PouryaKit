using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.RabbitMQ;

namespace Rmq.Kit;

/// <summary>The two topologies the Spike compares.</summary>
public enum Topology
{
    /// <summary>A: one broker queue per subscription, bound to the wire name's exchange, each with a durable Inbox.</summary>
    QueuePerSubscription,

    /// <summary>
    /// B: one broker queue per Host with a durable Inbox, bound to every subscribed wire name's exchange; each envelope
    /// received there is published again to one durable local queue per subscription (ADR 0006's local mechanism).
    /// </summary>
    ReceiveOnceThenLocalQueues,
}

public static class BrokerTopology
{
    /// <summary>
    /// The publishing Host: one fanout exchange per published wire name, routed from the publication, with a durable
    /// outbox. Nothing here depends on who subscribes, or where.
    /// </summary>
    public static void ConfigureProducer(WolverineOptions wolverine, KitMessaging messaging, Uri broker, bool routeToBroker)
    {
        wolverine.UseRabbitMq(broker).AutoProvision().DisableDeadLetterQueueing();
        if (!routeToBroker)
        {
            return;
        }

        foreach (var (type, wireName) in messaging.Publications)
        {
            wolverine
                .PublishMessage(type)
                .ToRabbitExchange(wireName, exchange => exchange.ExchangeType = ExchangeType.Fanout)
                .UseDurableOutbox();
        }
    }

    /// <summary>
    /// The subscribing Host. Declares its queues and their bindings, a durable Inbox on every listener, the routes and
    /// handler the topology needs, and the Kit's stored retry. Dead letters go to Wolverine's storage, not a broker queue.
    /// </summary>
    public static void ConfigureConsumer(
        WolverineOptions wolverine,
        KitMessaging messaging,
        Uri broker,
        Topology topology,
        string hostQueue,
        TimeSpan[] retryDelays
    )
    {
        var rabbit = wolverine.UseRabbitMq(broker).AutoProvision().DisableDeadLetterQueueing();
        var endpoints = new EndpointMap();
        switch (topology)
        {
            case Topology.QueuePerSubscription:
                foreach (var subscription in messaging.Subscriptions)
                {
                    rabbit.BindExchange(WireNames.Of(subscription.MessageType), ExchangeType.Fanout).ToQueue(subscription.Name);
                    wolverine.ListenToRabbitQueue(subscription.Name).UseDurableInbox();
                    endpoints.Deliver(RabbitMqEndpointUri.Queue(subscription.Name), subscription);
                }

                break;

            case Topology.ReceiveOnceThenLocalQueues:
                foreach (var wireName in messaging.Subscriptions.Select(s => WireNames.Of(s.MessageType)).Distinct())
                {
                    rabbit.BindExchange(wireName, ExchangeType.Fanout).ToQueue(hostQueue);
                }

                wolverine.ListenToRabbitQueue(hostQueue).UseDurableInbox();
                endpoints.FanOut(RabbitMqEndpointUri.Queue(hostQueue));
                foreach (var subscription in messaging.Subscriptions)
                {
                    wolverine.PublishMessage(subscription.MessageType).ToLocalQueue(subscription.Name);
                    endpoints.Deliver(new Uri($"local://{subscription.Name}/"), subscription);
                }

                break;
        }

        foreach (var messageType in messaging.Subscriptions.Select(s => s.MessageType).Distinct())
        {
            wolverine.AddMessageHandler(messageType, new SubscriptionDelivery(endpoints));
        }

        wolverine.OnException<Exception>().ScheduleRetry(retryDelays).Then.MoveToErrorQueue();
    }
}
