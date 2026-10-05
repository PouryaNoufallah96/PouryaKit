using Microsoft.EntityFrameworkCore;
using Wolverine.EntityFrameworkCore;

namespace S2.Kit;

/// <summary>Messages an Operation asks the Unit of Work to publish. Feature code never touches the outbox.</summary>
public sealed class Outgoing
{
    internal List<object> Messages { get; } = [];

    public void Add(object message) => Messages.Add(message);
}

/// <summary>
/// The owned Unit of Work from Spike S1 (ADR 0004), reduced: begin, run the Operation, hand each published message
/// to the delivery option inside the transaction, save, commit, and only then flush.
/// </summary>
public sealed class UnitOfWork<TDbContext>(IDbContextOutbox<TDbContext> outbox, IDeliveryOption delivery)
    where TDbContext : DbContext
{
    public async Task ExecuteAsync(Func<TDbContext, Outgoing, Task> operation, Func<Task>? afterCommitBeforeFlush = null)
    {
        var db = outbox.DbContext;
        var outgoing = new Outgoing();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await operation(db, outgoing);
            foreach (var message in outgoing.Messages) await delivery.PublishAsync(outbox, message);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (afterCommitBeforeFlush is not null) await afterCommitBeforeFlush();
        await outbox.FlushOutgoingMessagesAsync();
    }
}
