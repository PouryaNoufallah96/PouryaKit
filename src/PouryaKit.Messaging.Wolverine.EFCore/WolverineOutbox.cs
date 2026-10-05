using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;
using Wolverine.Util;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// Writes each Domain Event and each Integration Event the Operation published that has a subscription as one durable
// envelope per subscription, on the Unit of Work's own connection and transaction (ADR 0004), carrying the Operation
// Context, and an Integration Event its identity too, and sends them after the commit.
internal sealed partial class WolverineOutbox(
    IWolverineRuntime runtime,
    IServiceProvider scope,
    PublishedIntegrationEvents integrationEvents,
    ILogger<WolverineOutbox> logger
) : IUnitOfWorkOutbox,
    INotificationOutbox
{
    private DbContextOutbox? _written;

    public async Task WriteAsync(DbContext dbContext, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(messages);
        _written = null;

        // A message has a route for each of its subscriptions, and none without one.
        List<(object Message, Guid? Identity)> published =
        [
            .. messages.Select(message => (message, (Guid?)null)),
            .. integrationEvents.Take().Select(e => (e.IntegrationEvent, (Guid?)e.DomainEventId)),
        ];
        published.RemoveAll(message => runtime.RoutingFor(message.Message.GetType()).Routes.Length == 0);
        if (published.Count == 0)
        {
            return;
        }

        // A new outbox for every commit attempt: one whose transaction rolled back still holds its envelopes, and would
        // send them with its next flush.
        var outbox = new DbContextOutbox(runtime, []);
        outbox.Enroll(dbContext);
        var context = scope.GetRequiredService<OperationContext>();
        foreach (var (message, identity) in published)
        {
            var options = new DeliveryOptions().WithOperationContext(context);
            if (identity is { } domainEventId)
            {
                await RecordPublicationAsync(message, domainEventId, cancellationToken);
                options.Headers[IntegrationEventHeaders.DomainEventId] = domainEventId.ToString();
            }

            await outbox.PublishAsync(message, options);
        }

        _written = outbox;
    }

    // Two Notification Handlers of one Domain Event that publish one type would send two Integration Events with one
    // identity, and every receiver's Receipt would drop the second. The publishing Module records each publication, in
    // its own Receipt table and in this transaction, under a key no subscription name can take (a name has no '#'), so
    // the second delivery to commit fails instead.
    private async Task RecordPublicationAsync(object integrationEvent, Guid domainEventId, CancellationToken cancellationToken)
    {
        var wireName = integrationEvent.GetType().ToMessageTypeName();
        // Resolved here, not injected: the Receipts depend on the Unit of Work, which depends on this Outbox.
        var receipts = scope.GetRequiredService<IReceipts>();
        if (!await receipts.RecordAsync($"{wireName.ToLowerInvariant()}#published", domainEventId, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Another Notification Handler of the Domain Event {domainEventId} already published {wireName}. One Domain Event yields at most one Integration Event of each type: both would carry its identity, and every receiver would apply only the first."
            );
        }
    }

    public async Task FlushAsync()
    {
        if (_written is not { } outbox)
        {
            return;
        }

        _written = null;
        try
        {
            await outbox.FlushOutgoingMessagesAsync();
        }
        catch (Exception exception)
        {
            // The change committed; reporting a failure now would invite the caller to make it twice.
            LogNotSent(logger, exception);
        }
    }

    [LoggerMessage(
        EventName = "NotSent",
        Level = LogLevel.Warning,
        Message = "Notifications and Integration Events committed with the Operation could not be sent; the message store recovers them after the inbox stale time"
    )]
    private static partial void LogNotSent(ILogger logger, Exception exception);
}
