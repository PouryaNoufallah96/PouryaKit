using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.AI;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Persistence;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.AI.Postgres;

// Writes join the Unit of Work's transaction, so a stored embedding commits or rolls back with the Operation. Under read
// committed, a second Operation storing the same tenant and key waits on the first's row until it ends, then inserts
// when it rolled back and replaces its row when it committed, so the later commit wins: PostgreSQL's documented
// behaviour of insert on conflict, not shown by a Kit test. A search reads on the Module's
// own connection, which inside a command is the Unit of Work's, so it sees what its Operation stored. The tenant is the
// Operation's, as the Dapper lease binds it, never the caller's. The embedding goes as a real[], which pgvector casts to
// its vector, so no connection needs a type mapping of its own.
internal sealed class PostgresVectorStore(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    IUnitOfWorkConnection unitOfWorkConnection,
    IUnitOfWork unitOfWork,
    Operation operation
) : IVectorStore
{
    private string Tenant => operation.Context?.Tenant ?? OperationContext.NoTenant;

    public async Task StoreAsync(
        string collection,
        string key,
        string content,
        CancellationToken cancellationToken
    )
    {
        var table = VectorCollection.Table(collection);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(content);
        var (connection, transaction) = unitOfWorkConnection.Join();
        var embedding = await generator.GenerateVectorAsync(
            content,
            cancellationToken: cancellationToken
        );
        await ExecuteAsync(
            connection,
            transaction,
            collection,
            $"insert into {table} (tenant, key, content, embedding) values ($1, $2, $3, $4) on conflict (tenant, key) do update set content = excluded.content, embedding = excluded.embedding",
            [Tenant, key, content, embedding.ToArray()],
            command => command.ExecuteNonQueryAsync(cancellationToken)
        );
    }

    public async Task RemoveAsync(
        string collection,
        string key,
        CancellationToken cancellationToken
    )
    {
        var table = VectorCollection.Table(collection);
        ArgumentNullException.ThrowIfNull(key);
        var (connection, transaction) = unitOfWorkConnection.Join();
        await ExecuteAsync(
            connection,
            transaction,
            collection,
            $"delete from {table} where tenant = $1 and key = $2",
            [Tenant, key],
            command => command.ExecuteNonQueryAsync(cancellationToken)
        );
    }

    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(
        string collection,
        string query,
        int top,
        CancellationToken cancellationToken
    )
    {
        var table = VectorCollection.Table(collection);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(top, 1);
        var database = (
            EfUnitOfWorkDbContext.Of(unitOfWork)
            ?? throw new InvalidOperationException(
                $"Searching a vector collection needs the EF Core Unit of Work of a Module stored with UsePouryaKitPostgres, but the scope's Unit of Work is {unitOfWork.GetType().Name}."
            )
        ).Database;
        var embedding = await generator.GenerateVectorAsync(
            query,
            cancellationToken: cancellationToken
        );
        await database.OpenConnectionAsync(cancellationToken);
        try
        {
            return await ExecuteAsync(
                database.GetDbConnection(),
                database.CurrentTransaction?.GetDbTransaction(),
                collection,
                $"select key, content, embedding <=> $2::vector as distance from {table} where tenant = $1 order by distance limit $3",
                [Tenant, embedding.ToArray(), top],
                async command =>
                {
                    List<VectorMatch> matches = [];
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        matches.Add(
                            new(reader.GetString(0), reader.GetString(1), reader.GetDouble(2))
                        );
                    }

                    return matches;
                }
            );
        }
        finally
        {
            await database.CloseConnectionAsync();
        }
    }

    private static async Task<T> ExecuteAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string collection,
        string sql,
        object[] values,
        Func<DbCommand, Task<T>> execute
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var value in values)
        {
            var parameter = command.CreateParameter();
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        try
        {
            return await execute(command);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new InvalidOperationException(
                $"There is no {VectorCollectionTable.Schema}.{collection} table to keep the vector collection {collection} in. Add a migration to the Module that calls CreateVectorCollection with its name and the embedding model's dimensions.",
                exception
            );
        }
    }
}
