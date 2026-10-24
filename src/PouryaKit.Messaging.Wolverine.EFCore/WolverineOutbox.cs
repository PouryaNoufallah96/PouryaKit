using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;
using Wolverine.EntityFrameworkCore;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// Writes what the Operation published, and the Snapshot of each aggregate it changed, through the store-neutral
// OutboxWriter on the Unit of Work's own connection and transaction (ADR 0004), and sends it after the commit. The
// Snapshots are built here, from the EF Core entries the prepare step advanced; every header, route and publication
// record is the writer's.
internal sealed partial class WolverineOutbox(
    IWolverineRuntime runtime,
    OutboxWriter writer,
    ISnapshotMappers snapshotMappers,
    ILogger<WolverineOutbox> logger
) : IUnitOfWorkOutbox,
    IOutbox
{
    private MessageContext? _written;

    public async Task WriteAsync(
        DbContext dbContext,
        IReadOnlyList<object> messages,
        IReadOnlyList<EntityEntry> changedAggregates,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(changedAggregates);
        _written = null;
        _written = await writer.WriteAsync(
            () =>
            {
                // A new outbox for every commit attempt: one whose transaction rolled back still holds its envelopes, and
                // would send them with its next flush.
                var outbox = new DbContextOutbox(runtime, []);
                outbox.Enroll(dbContext);
                return outbox;
            },
            messages,
            SnapshotStep.Build(dbContext, changedAggregates, snapshotMappers),
            cancellationToken
        );
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
        Message = "Notifications, Integration Events and Snapshots committed with the Operation could not be sent; the message store recovers them after the inbox stale time"
    )]
    private static partial void LogNotSent(ILogger logger, Exception exception);
}
