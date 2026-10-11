using System.Diagnostics;
using System.Globalization;
using PouryaKit.Cqrs;
using Wolverine;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Carries an Operation Context in the headers of Wolverine's <see cref="DeliveryOptions"/>, as part of the Messaging
/// Capability, so the work a message causes continues that Operation.
/// </summary>
public static class OperationContextHeaderExtensions
{
    extension(DeliveryOptions options)
    {
        /// <summary>
        /// Carries <paramref name="context"/> in the <see cref="OperationContextHeaders"/> of the messages sent with
        /// these options, so the work they cause continues that Operation. The Unit of Work's Outbox calls it; Feature
        /// code never does.
        /// </summary>
        public DeliveryOptions WithOperationContext(OperationContext context)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(context);
            options.Headers[OperationContextHeaders.ActorId] = context.Actor.Id;
            options.Headers[OperationContextHeaders.ActorKind] = context.Actor.Kind.ToString();
            if (context.Tenant is not null)
            {
                options.Headers[OperationContextHeaders.Tenant] = context.Tenant;
            }

            options.Headers[OperationContextHeaders.CorrelationId] = context.CorrelationId.ToString();
            options.Headers[OperationContextHeaders.CausationId] = context.OperationId.ToString();
            return options;
        }
    }

    // The context of the Operation a delivery runs: a trusted continuation of the one that published the message, with
    // the same Actor, tenant and correlation, caused by it, and an OperationId of its own. Trusted because the Kit's
    // Outbox wrote these headers when that authorized Operation committed. Beside it, the trace context of the activity
    // current at publish: Wolverine's parent-id with the Kit's tracestate, default when the envelope names no parent. A
    // copy fanned out from another Host's message has no parent-id of its own unless Wolverine's span was current at the
    // fan-out; the Kit's traceparent header then names the publishing Operation.
    internal static (OperationContext Context, ActivityContext TraceParent) ContinuedOperationContext(Envelope envelope)
    {
        string Header(string name) =>
            envelope.Headers.TryGetValue(name, out var value) && value is not null
                ? value
                : throw new InvalidOperationException(
                    $"The message {envelope.Id} has no {name} header, so it carries no Operation Context to continue. Only the Unit of Work's Outbox sends Notifications and Integration Events."
                );

        // The exact names WithOperationContext writes, and nothing else, as Web's OperationContextHttpHeaders.Continued
        // reads its header: Cqrs opens its internals for RestoreTrusted alone (ADR 0007), so the two cannot share one.
        var actor = Header(OperationContextHeaders.ActorKind) switch
        {
            nameof(ActorKind.Person) => Actor.Person(Header(OperationContextHeaders.ActorId)),
            nameof(ActorKind.System) => Actor.System(Header(OperationContextHeaders.ActorId)),
            _ => throw new InvalidOperationException($"The message {envelope.Id} has an undefined ActorKind header."),
        };
        envelope.Headers.TryGetValue(OperationContextHeaders.Tenant, out var tenant);
        envelope.Headers.TryGetValue(OperationContextHeaders.TraceState, out var traceState);
        var parent = envelope.ParentId ?? envelope.Headers.GetValueOrDefault(OperationContextHeaders.TraceParent);
        ActivityContext.TryParse(parent, traceState, isRemote: true, out var traceParent);
        return (
            OperationContext.RestoreTrusted(
                actor,
                tenant,
                Guid.Parse(Header(OperationContextHeaders.CorrelationId), CultureInfo.InvariantCulture),
                Guid.Parse(Header(OperationContextHeaders.CausationId), CultureInfo.InvariantCulture)
            ),
            traceParent
        );
    }
}
