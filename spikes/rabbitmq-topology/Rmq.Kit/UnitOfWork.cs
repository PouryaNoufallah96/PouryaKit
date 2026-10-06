using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Rmq.Kit;

/// <summary>An Integration Event an Operation asks the Unit of Work to publish, with the Domain Event id it follows from.</summary>
public sealed record Published(object Message, Guid DomainEventId);

/// <summary>
/// The owned Unit of Work of ADR 0004, reduced: begin, run the Operation, publish through the EF outbox inside the
/// transaction, save, commit, and only then flush. Feature code never touches the outbox.
/// </summary>
public sealed class UnitOfWork<TDbContext>(IDbContextOutbox<TDbContext> outbox)
    where TDbContext : DbContext
{
    public async Task ExecuteAsync(Func<TDbContext, List<Published>, Task> operation, Func<Task>? afterCommitBeforeFlush = null)
    {
        var db = outbox.DbContext;
        List<Published> published = [];
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await operation(db, published);
            foreach (var (message, domainEventId) in published)
            {
                var options = new DeliveryOptions();
                options.Headers[KitHeaders.DomainEventId] = domainEventId.ToString();
                await outbox.PublishAsync(message, options);
            }

            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (afterCommitBeforeFlush is not null)
        {
            await afterCommitBeforeFlush();
        }

        await outbox.FlushOutgoingMessagesAsync();
    }
}
