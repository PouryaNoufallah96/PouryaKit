using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// Writes each message that has a subscription as one durable envelope per subscription, on the Unit of Work's own
// connection and transaction (ADR 0004), carrying the Operation Context, and sends them after the commit.
internal sealed partial class WolverineOutbox(IWolverineRuntime runtime, IServiceProvider scope, ILogger<WolverineOutbox> logger)
    : IUnitOfWorkOutbox
{
    private DbContextOutbox? _written;

    public async Task WriteAsync(DbContext dbContext, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(messages);
        _written = null;

        // A message has a route for each of its subscriptions, and none without one.
        var published = messages.Where(message => runtime.RoutingFor(message.GetType()).Routes.Length > 0).ToList();
        if (published.Count == 0)
        {
            return;
        }

        // A new outbox for every commit attempt: one whose transaction rolled back still holds its envelopes, and would
        // send them with its next flush.
        var outbox = new DbContextOutbox(runtime, []);
        outbox.Enroll(dbContext);
        var context = scope.GetRequiredService<OperationContext>();
        foreach (var message in published)
        {
            await outbox.PublishAsync(message, new DeliveryOptions().WithOperationContext(context));
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
        Message = "Notifications committed with the Operation could not be sent; the message store recovers them after the inbox stale time"
    )]
    private static partial void LogNotSent(ILogger logger, Exception exception);
}
