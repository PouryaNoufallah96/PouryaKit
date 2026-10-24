using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Persistence.Postgres;

// A scope key's number is its row in the Module's own schema, taken on the Unit of Work's transaction, so the row stays
// locked until that transaction ends. Under read committed, when another transaction holds the row between these
// statements: the insert waits for an uncommitted insert of the same key, then inserts nothing when that transaction
// commits and inserts the row when it rolls back; the update waits on the row lock, then increments the value that
// transaction committed, or the value before it when it rolled back. An Operation whose caller cancels is rolled back,
// so its number is given back as on any rollback, as
// NumberSequenceTests.A_cancelled_operation_gives_its_number_back_to_the_next_operation shows. A transaction whose
// connection drops before its commit is rolled back by the server; that is PostgreSQL's documented behaviour, not shown
// by a Kit test. The increment is relative, never a value read first: a value read before the wait would give out the
// other transaction's number again.
internal sealed class PostgresNumberSequences(IUnitOfWorkConnection unitOfWorkConnection, IUnitOfWork unitOfWork)
    : INumberSequences
{
    public async Task<Result<long>> NextAsync(string scopeKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        // Refuses code that only reads, an inactive Unit of Work and any but the EF Core one.
        var (connection, transaction) = unitOfWorkConnection.Join();
        var dbContext = EfUnitOfWorkDbContext.Of(unitOfWork)!;
        var table = NumberSequenceTable.In(dbContext);
        await using var batch = new NpgsqlBatch((NpgsqlConnection)connection, (NpgsqlTransaction)transaction)
        {
            BatchCommands =
            {
                new($"insert into {table} (scope_key, last_value) values ($1, 0) on conflict do nothing")
                {
                    Parameters = { new() { Value = scopeKey } },
                },
                new($"update {table} set last_value = last_value + 1 where scope_key = $1 returning last_value")
                {
                    Parameters = { new() { Value = scopeKey } },
                },
            },
        };
        try
        {
            return (long)(await batch.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            return UnitOfWorkErrors.ConcurrencyConflict;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidOperationException(
                $"{dbContext.GetType().Name} has no {table} table to keep its number sequences in. Add a migration to the Module that calls CreateNumberSequenceTable with its schema.",
                exception
            );
        }
    }
}
