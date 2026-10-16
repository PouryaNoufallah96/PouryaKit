using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Util;

namespace M2.Marten;

// The part of the Kit's WolverineOutbox.WriteAsync that names no store, copied: the Domain Events handled and the
// Integration Events published, each with a route for each subscription and none without one, carrying the Operation
// Context and the trace state, an Integration Event its Domain Event's identity and its publication record. It writes
// through any Wolverine MessageContext whose envelope transaction is enrolled in a store: DbContextOutbox and MartenOutbox
// both are one. Snapshots and delayed commands are left out of the copy; their list is the same shape.
internal sealed class StoreNeutralOutbox(IWolverineRuntime runtime, IServiceProvider scope)
{
    // The Kit's scope buffer of published Integration Events is internal to its EF package; read through reflection here,
    // it is the one buffer both outboxes take from.
    private static readonly Type Published = typeof(WolverineEfCoreHostApplicationBuilderExtensions).Assembly.GetType(
        "PouryaKit.Messaging.Wolverine.EFCore.PublishedIntegrationEvents",
        throwOnError: true
    )!;

    public async Task WriteAsync(MessageContext outbox, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        var taken = (List<(Guid DomainEventId, object IntegrationEvent)>)Published.GetMethod("Take")!.Invoke(scope.GetRequiredService(Published), null)!;
        List<(object Message, Guid? DomainEventId)> published =
        [
            .. messages.Select(message => (message, (Guid?)null)),
            .. taken.Select(e => (e.IntegrationEvent, (Guid?)e.DomainEventId)),
        ];
        published.RemoveAll(message => runtime.RoutingFor(message.Message.GetType()).Routes.Length == 0);

        var context = scope.GetRequiredService<OperationContext>();
        var traceState = Activity.Current?.TraceStateString;
        foreach (var (message, domainEventId) in published)
        {
            var options = new DeliveryOptions().WithOperationContext(context);
            if (traceState is { Length: > 0 })
            {
                options.Headers[OperationContextHeaders.TraceState] = traceState;
            }

            if (domainEventId is { } publishedFor)
            {
                await RecordPublicationAsync(message, publishedFor, cancellationToken);
                options.Headers[IntegrationEventHeaders.DomainEventId] = publishedFor.ToString();
            }

            await outbox.PublishAsync(message, options);
        }
    }

    private async Task RecordPublicationAsync(object integrationEvent, Guid domainEventId, CancellationToken cancellationToken)
    {
        var wireName = integrationEvent.GetType().ToMessageTypeName();
        if (!await scope.GetRequiredService<IReceipts>().RecordAsync($"{wireName.ToLowerInvariant()}#published", domainEventId, cancellationToken))
        {
            throw new InvalidOperationException($"Another Notification Handler of the Domain Event {domainEventId} already published {wireName}.");
        }
    }
}
