using System.Globalization;
using PouryaKit.Cqrs;
using Wolverine;

namespace PouryaKit.Messaging.Wolverine;

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

    // The context of the Operation a delivery runs: a continuation of the one that published the message, with the same
    // Actor, tenant and correlation, caused by it, and an OperationId of its own.
    internal static OperationContext ContinuedOperationContext(Envelope envelope)
    {
        string Header(string name) =>
            envelope.Headers.TryGetValue(name, out var value) && value is not null
                ? value
                : throw new InvalidOperationException(
                    $"The message {envelope.Id} has no {name} header, so it carries no Operation Context to continue. Only the Unit of Work's Outbox sends Notifications and Integration Events."
                );

        var actor = Enum.Parse<ActorKind>(Header(OperationContextHeaders.ActorKind)) switch
        {
            ActorKind.Person => Actor.Person(Header(OperationContextHeaders.ActorId)),
            _ => Actor.System(Header(OperationContextHeaders.ActorId)),
        };
        envelope.Headers.TryGetValue(OperationContextHeaders.Tenant, out var tenant);
        return new OperationContext(
            actor,
            tenant,
            Guid.Parse(Header(OperationContextHeaders.CorrelationId), CultureInfo.InvariantCulture),
            Guid.Parse(Header(OperationContextHeaders.CausationId), CultureInfo.InvariantCulture)
        );
    }
}
