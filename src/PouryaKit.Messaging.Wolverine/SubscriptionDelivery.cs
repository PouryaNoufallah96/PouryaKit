using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports;

namespace PouryaKit.Messaging.Wolverine;

// The one pre-built handler of a message type, for every subscription to it: Wolverine routes one envelope to each
// subscription's own queue, so the queue an envelope arrived on names the subscription, and each is retried and
// acknowledged on its own. It generates no code, so the message and its handlers may be internal.
internal sealed class SubscriptionDelivery(Subscriptions subscriptions) : MessageHandler
{
    private const string KitHeaders = "pouryakit-";

    public override async Task HandleAsync(MessageContext context, CancellationToken cancellation)
    {
        var envelope = context.Envelope!;
        // An envelope from another Host arrives once, at this Host's queue on a broker (ADR 0006, ticket #50 amendment),
        // and is published again here with the Kit's headers: one copy per subscription on its local queue, stored before
        // this envelope is marked handled, so a crash in between fans it out again and each copy finds its Receipt.
        if (envelope.Destination!.Scheme != TransportConstants.Local)
        {
            DeliveryOptions copies = new();
            foreach (var (name, value) in envelope.Headers.Where(header => header.Key.StartsWith(KitHeaders, StringComparison.Ordinal)))
            {
                copies.Headers[name] = value;
            }

            await context.PublishAsync(envelope.Message!, copies);
            return;
        }

        // A local queue's address is local://{subscription, lower-cased}/.
        var subscription = subscriptions.Named(envelope.Destination!.Host);
        var (operationContext, traceParent) = OperationContextHeaderExtensions.ContinuedOperationContext(envelope);
        var message = envelope.Message!;
        // Not the envelope's id: every subscription's envelope of one message has an id of its own.
        var deliver = message switch
        {
            DomainEvent domainEvent => new Deliver(subscription, message, domainEvent.Id),
            _ when subscriptions.IsSnapshot(message.GetType()) => SnapshotDelivery(subscription, envelope),
            _ => new Deliver(subscription, message, IntegrationEventIdentity(envelope)),
        };

        // Each attempt runs in a scope of its own: nothing from a failed attempt reaches the next.
        await using var scope = context.Runtime.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(operationContext, subscription.Module, traceParent);
        var result = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(deliver, cancellation);
        if (result.IsFailure)
        {
            throw subscription.Failed(result.Errors);
        }
    }

    private static Guid IntegrationEventIdentity(Envelope envelope) =>
        envelope.Headers.TryGetValue(IntegrationEventHeaders.DomainEventId, out var value) && value is not null
            ? Guid.Parse(value, CultureInfo.InvariantCulture)
            : throw new InvalidOperationException(
                $"The Integration Event {envelope.Id} has no {IntegrationEventHeaders.DomainEventId} header, so its Receipt has no identity to record. Only the Unit of Work's Outbox sends Integration Events."
            );

    // A Snapshot's identity is a function of what it is, the contract's wire name, the aggregate's key and the version,
    // so every delivery of it repeats it and two versions of one aggregate never share it: a name-based UUID (RFC 9562
    // version 8) from their SHA-256.
    private static Deliver SnapshotDelivery(Subscription subscription, Envelope envelope)
    {
        string Header(string name) =>
            envelope.Headers.TryGetValue(name, out var value) && value is not null
                ? value
                : throw new InvalidOperationException(
                    $"The Snapshot {envelope.Id} has no {name} header, so it cannot be told apart from another version of its aggregate. Only the Unit of Work's Outbox sends Snapshots."
                );

        var of = new SnapshotOf(
            Header(SnapshotHeaders.AggregateId),
            long.Parse(Header(SnapshotHeaders.Version), CultureInfo.InvariantCulture)
        );
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(
            Encoding.UTF8.GetBytes($"{envelope.MessageType!.ToLowerInvariant()}\n{of.AggregateId}\n{of.Version.ToString(CultureInfo.InvariantCulture)}"),
            hash
        );
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Deliver(subscription, envelope.Message!, new Guid(hash[..16], bigEndian: true), of);
    }
}
