using Wolverine;
using Wolverine.RabbitMQ;

namespace PouryaKit.Messaging.Wolverine.RabbitMq;

// Applied when Wolverine's options are first built, after every Composition Root ran, so every publication and
// subscription of the Host is known (ADR 0006's RabbitMQ topology amendment, topology B).
internal sealed class RabbitMqExtension(
    string connectionString,
    string serviceName,
    IWireNames wireNames
) : IWolverineExtension
{
    public void Configure(WolverineOptions wolverine)
    {
        wolverine.ServiceName = serviceName;
        // Dead letters stay in the message store, where the Kit's subscriptions keep theirs.
        var rabbit = wolverine
            .UseRabbitMq(connectionString)
            .AutoProvision()
            .DisableDeadLetterQueueing();
        // From the publication, never from a subscription, so the Outbox writes each one in the Unit of Work's commit
        // whether or not a subscription of this Host receives it.
        foreach (var (type, wireName) in wireNames.Published)
        {
            wolverine
                .PublishMessage(type)
                .ToRabbitExchange(wireName, exchange => exchange.ExchangeType = ExchangeType.Fanout)
                .UseDurableOutbox();
        }

        // One queue for the Host, bound only to what other Hosts publish: what it publishes itself reaches its
        // subscriptions locally, and bound here it would arrive twice.
        foreach (var wireName in wireNames.Received)
        {
            rabbit.BindExchange(wireName, ExchangeType.Fanout).ToQueue(serviceName);
        }

        wolverine.ListenToRabbitQueue(serviceName).UseDurableInbox();
    }
}
