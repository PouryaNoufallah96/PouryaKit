using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The Snapshot and send steps of the Unit of Work's commit sequence: what an Operation publishes, its Snapshots included, is written
/// to the Outbox in the Unit of Work's transaction, so it exists if and only if the change commits, and is sent only
/// after the commit. A messaging package registers it, scoped; the EF Core Unit of Work calls it when one is registered.
/// Feature code never does.
/// </summary>
public interface IUnitOfWorkOutbox
{
    /// <summary>
    /// Writes each of <paramref name="messages"/> that has a subscription, and the Snapshots of
    /// <paramref name="changedAggregates"/> that have one, through <paramref name="dbContext"/>'s open transaction, once
    /// per commit attempt, after persistence is prepared and before the save. The messages are the Domain Events the
    /// Operation handled, in the order handled. The changed aggregates are those whose version the prepare step
    /// advanced, each once, in their final state: a Snapshot is built from them as they are and must change nothing.
    /// </summary>
    Task WriteAsync(
        DbContext dbContext,
        IReadOnlyList<object> messages,
        IReadOnlyList<EntityEntry> changedAggregates,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Sends what <see cref="WriteAsync"/> wrote, only after the transaction committed. It never throws: the change has
    /// committed, and a message that was not sent stays in the Outbox, from which it is recovered.
    /// </summary>
    Task FlushAsync();
}
