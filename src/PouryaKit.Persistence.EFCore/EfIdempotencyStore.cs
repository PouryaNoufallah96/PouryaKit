using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

// A key is a row in the Module's own schema, written on its Unit of Work's transaction. Under read committed, inserting
// a key another transaction holds waits for that transaction, then inserts nothing when it committed and the key when
// it rolled back. ON CONFLICT DO NOTHING leaves this transaction usable either way, where a unique violation would abort
// it before the committed success could be read.
internal sealed class EfIdempotencyStore(IUnitOfWork unitOfWork) : IIdempotencyStore
{
    private const string UndefinedTable = "42P01";

    private const string Key = "tenant = {0} and actor_kind = {1} and actor_id = {2} and command = {3} and request_id = {4}";

    public async Task<RecordedSuccess?> ReserveAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        var reserved = await ExecuteAsync(
            table =>
                $"insert into {table} (tenant, actor_kind, actor_id, command, request_id) values ({{0}}, {{1}}, {{2}}, {{3}}, {{4}}) on conflict do nothing",
            (database, sql) => database.ExecuteSqlRawAsync(sql, Values(key), cancellationToken)
        );
        if (reserved == 1)
        {
            return null;
        }

        var recorded = await ExecuteAsync(
            table => $"select response as \"Value\" from {table} where {Key}",
            (database, sql) => database.SqlQueryRaw<string?>(sql, Values(key)).ToListAsync(cancellationToken)
        );
        return recorded is [var response]
            ? new RecordedSuccess(response)
            : throw new InvalidOperationException(
                $"The key of request {key.RequestId} for {key.Command} was held by another Operation and is gone. Rows of {IdempotencyTable.Name} are never removed while a repeat of their request may still arrive."
            );
    }

    public async Task RecordAsync(IdempotencyKey key, string response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(response);
        var recorded = await ExecuteAsync(
            table => $"update {table} set response = {{5}} where {Key}",
            (database, sql) => database.ExecuteSqlRawAsync(sql, [.. Values(key), response], cancellationToken)
        );
        if (recorded != 1)
        {
            throw new InvalidOperationException(
                $"The key of request {key.RequestId} for {key.Command} was not reserved in this Unit of Work's transaction, so its success cannot be recorded."
            );
        }
    }

    private static object[] Values(IdempotencyKey key) =>
        [key.Tenant ?? IdempotencyTable.NoTenant, key.Actor.Kind.ToString(), key.Actor.Id, key.Command, key.RequestId];

    private async Task<T> ExecuteAsync<T>(Func<string, string> sql, Func<DatabaseFacade, string, Task<T>> run)
    {
        var dbContext =
            unitOfWork.DbContext
            ?? throw new InvalidOperationException(
                $"Idempotency keys are kept in the EF Core Unit of Work's transaction, but the scope's Unit of Work is {unitOfWork.GetType().Name}."
            );
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "No Unit of Work is active, so there is no transaction to keep an idempotency key in."
            );
        }

        // The table name comes from the Module's model, never from input; the values are parameters.
        var table = dbContext.Model.GetDefaultSchema() is { } schema
            ? $"\"{schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".{IdempotencyTable.Name}"
            : IdempotencyTable.Name;
        var statement = sql(table);
        try
        {
            return await run(dbContext.Database, statement);
        }
        catch (DbException exception) when (exception.SqlState == UndefinedTable)
        {
            throw new InvalidOperationException(
                $"{dbContext.GetType().Name} has no {table} table to keep the keys of its [Idempotent] commands in. Add a migration to the Module that calls CreateIdempotencyTable with its schema.",
                exception
            );
        }
    }
}
