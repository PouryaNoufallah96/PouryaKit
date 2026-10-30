using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Inventory.StockItems.Features.ReconcileStock;

#region reconcile-stock-handler
// Records, at each occurrence, how many stock items Inventory keeps, and on whose authority: the system Actor the
// Composition Root declared, since no person asked for it. Only the effect: the Kit records the occurrence's Receipt,
// stores the next occurrence and commits, so an occurrence delivered twice records one reconciliation.
internal sealed class ReconcileStockCommandHandler(IUnitOfWorkConnection unitOfWork, OperationContext context)
    : CommandHandler<ReconcileStockCommand>
{
    public override async Task<Result> Handle(ReconcileStockCommand command, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "insert into inventory.stock_reconciliations (occurrence, stock_items, reconciled_by) select @Occurrence, count(*), @ReconciledBy from inventory.stock_items",
                new { command.Occurrence, ReconciledBy = context.Actor.Id },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return Result.Success();
    }
}
#endregion
