using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

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
                options.Headers[IntegrationEventHeaders.DomainEventId] = domainEventId.ToString();
            }

            await outbox.PublishAsync(message, options);
        }

        _written = outbox;
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
