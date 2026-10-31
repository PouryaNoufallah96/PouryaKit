using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// The store-neutral part of the Outbox write, one per scope: what an Operation published becomes one durable envelope
/// per subscription, through a Wolverine <see cref="MessageContext"/> its store package enrolled in the Unit of Work's
/// own transaction (ADR 0004, ADR 0011). A store package's Unit of Work calls it once per commit attempt, before its
/// commit; Feature code never does.
/// </summary>
public sealed class OutboxWriter
{
    private readonly IWolverineRuntime _runtime;
    private readonly IServiceProvider _scope;
    private readonly PublishedIntegrationEvents _integrationEvents;
    private readonly ScheduledDelayedCommands _delayedCommands;

    internal OutboxWriter(
        IWolverineRuntime runtime,
        IServiceProvider scope,
        PublishedIntegrationEvents integrationEvents,
        ScheduledDelayedCommands delayedCommands
    )
    {
        _runtime = runtime;
        _scope = scope;
        _integrationEvents = integrationEvents;
        _delayedCommands = delayedCommands;
    }

    /// <summary>
    /// Writes each of <paramref name="domainEvents"/>, the Domain Events the Operation handled in the order handled, each
    /// Integration Event its Notification Handler published, each of <paramref name="snapshots"/> the store package
    /// built, and each command the Operation scheduled, that has a subscription, through the context
    /// <paramref name="enroll"/> returns, created only when one has. Every envelope carries the Operation Context and the
    /// trace state of the current activity; an Integration Event also its Domain Event's identity, after its publication
    /// record in the publishing Module's <see cref="IReceipts"/>; a Snapshot its aggregate and version; a scheduled
    /// command the time it is due, and a recurring command's occurrence that time in its own header. Returns the context
    /// it wrote through, for the store package to flush after its commit, or null when nothing had a subscription. Throws
    /// when a second Notification Handler of one Domain Event published an Integration Event of a type already published
    /// for it.
    /// </summary>
    public async Task<MessageContext?> WriteAsync(
        Func<MessageContext> enroll,
        IReadOnlyList<object> domainEvents,
        IReadOnlyList<(object Snapshot, string AggregateId, long Version)> snapshots,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(enroll);
        ArgumentNullException.ThrowIfNull(domainEvents);
        ArgumentNullException.ThrowIfNull(snapshots);

        // A message has a route for each of its subscriptions, and none without one.
        List<(
            object Message,
            Guid? DomainEventId,
            Dictionary<string, string> Headers,
            DateTimeOffset? At
        )> published =
        [
            .. domainEvents.Select(message =>
                (message, (Guid?)null, new Dictionary<string, string>(), (DateTimeOffset?)null)
            ),
            .. _integrationEvents
                .Take()
                .Select(e =>
                    (
                        e.IntegrationEvent,
                        (Guid?)e.DomainEventId,
                        new Dictionary<string, string>
                        {
                            [IntegrationEventHeaders.DomainEventId] = e.DomainEventId.ToString(),
                        },
                        (DateTimeOffset?)null
                    )
                ),
            // A Snapshot needs no publication record: the Kit builds one per aggregate and contract in this commit, and
            // two commits of one version of an aggregate conflict, so its identity is never published twice.
            .. snapshots.Select(built =>
                (
                    built.Snapshot,
                    (Guid?)null,
                    new Dictionary<string, string>
                    {
                        [SnapshotHeaders.AggregateId] = built.AggregateId,
                        [SnapshotHeaders.Version] = built.Version.ToString(
                            CultureInfo.InvariantCulture
                        ),
                    },
                    (DateTimeOffset?)null
                )
            ),
            .. _delayedCommands
                .Take()
                .Select(scheduled =>
                    (
                        (object)scheduled.Command,
                        (Guid?)null,
                        scheduled.IsOccurrence
                            ? new Dictionary<string, string>
                            {
                                [DelayedCommandHeaders.Occurrence] = scheduled
                                    .At.ToUniversalTime()
                                    .ToString("O", CultureInfo.InvariantCulture),
                            }
                            : [],
                        (DateTimeOffset?)scheduled.At
                    )
                ),
        ];
        published.RemoveAll(message =>
            _runtime.RoutingFor(message.Message.GetType()).Routes.Length == 0
        );
        if (published.Count == 0)
        {
            return null;
        }

        var outbox = enroll();
        var context = _scope.GetRequiredService<OperationContext>();
        // Wolverine writes the current activity's id as each envelope's parent-id, but no tracestate (ADR 0009).
        var traceState = Activity.Current?.TraceStateString;
        foreach (var (message, domainEventId, headers, at) in published)
        {
            if (domainEventId is { } publishedFor)
            {
                await RecordPublicationAsync(message, publishedFor, cancellationToken);
            }

            var options = new DeliveryOptions { ScheduledTime = at }.WithOperationContext(context);
            if (traceState is { Length: > 0 })
            {
                options.Headers[OperationContextHeaders.TraceState] = traceState;
            }

            foreach (var (name, value) in headers)
            {
                options.Headers[name] = value;
            }

            await outbox.PublishAsync(message, options);
        }

        return outbox;
    }

    // Two Notification Handlers of one Domain Event that publish one type would send two Integration Events with one
    // identity, and every receiver's Receipt would drop the second. The publishing Module records each publication, in
    // its own Receipt store and in this transaction, under a key no subscription name can take (a name has no '#'), so
    // the second delivery to commit fails instead.
    private async Task RecordPublicationAsync(
        object integrationEvent,
        Guid domainEventId,
        CancellationToken cancellationToken
    )
    {
        var wireName = integrationEvent.GetType().ToMessageTypeName();
        // Resolved here, not injected: the Receipts depend on the Unit of Work, which depends on its Outbox.
        var receipts = _scope.GetRequiredService<IReceipts>();
        if (
            !await receipts.RecordAsync(
                $"{wireName.ToLowerInvariant()}#published",
                domainEventId,
                cancellationToken
            )
        )
        {
            throw new InvalidOperationException(
                $"Another Notification Handler of the Domain Event {domainEventId} already published {wireName}. One Domain Event yields at most one Integration Event of each type: both would carry its identity, and every receiver would apply only the first."
            );
        }
    }

    // An Integration Event a Saga's step emits (PouryaKit.Messaging.Wolverine.Sagas), through the context the Kit's saga
    // storage enrolled in the step's own transaction: the headers WriteAsync gives an Integration Event, for an Actor,
    // tenant, correlation and causation the step names, since no Operation runs it, and the trace context the step
    // continues, its parent written by the Kit only when no activity is current for Wolverine to write. No publication
    // record: the step is claimed in that transaction, and the saga refuses a second emission of one type from one step.
    internal static ValueTask WriteEmissionAsync(
        MessageContext outbox,
        object integrationEvent,
        (Actor Actor, string? Tenant, Guid CorrelationId, Guid CausationId) context,
        Guid identity,
        string? traceState,
        string? traceParent
    )
    {
        var options = OperationContextHeaderExtensions.WithContextHeaders(
            new DeliveryOptions(),
            context.Actor,
            context.Tenant,
            context.CorrelationId,
            context.CausationId
        );
        options.Headers[IntegrationEventHeaders.DomainEventId] = identity.ToString();
        if (traceState is { Length: > 0 })
        {
            options.Headers[OperationContextHeaders.TraceState] = traceState;
        }

        if (traceParent is not null && Activity.Current is null)
        {
            options.Headers[OperationContextHeaders.TraceParent] = traceParent;
        }

        return outbox.PublishAsync(integrationEvent, options);
    }
}
