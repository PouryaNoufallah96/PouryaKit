using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;
using PouryaKit.Persistence.Postgres;

namespace Inventory.StockCounts.Features.StartStockCount;

// Stock counts are numbered 1, 2, 3 within each year with no gap: the number is taken in this Operation's Unit of Work,
// so a count that fails gives its number back, and two counts started at once receive consecutive numbers.
internal sealed class StartStockCountCommandHandler(
    INumberSequences sequences,
    IUnitOfWorkConnection unitOfWork,
    OperationContext context
) : CommandHandler<StartStockCountCommand, long>
{
    public override async Task<Result<long>> Handle(
        StartStockCountCommand command,
        CancellationToken cancellationToken
    )
    {
        var year = DomainClock.Current.GetUtcNow().Year;
        var number = await sequences.NextAsync($"stock-count:{year}", cancellationToken);
        if (number.IsFailure)
        {
            return number;
        }

        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "insert into inventory.stock_counts (year, number, started_by) values (@Year, @Number, @StartedBy)",
                new
                {
                    Year = year,
                    Number = number.Value,
                    StartedBy = context.Actor.Id,
                },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return number;
    }
}
