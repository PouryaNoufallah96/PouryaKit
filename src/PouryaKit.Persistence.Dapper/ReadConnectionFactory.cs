using System.Data.Common;
using Npgsql;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Dapper;

// One per scope, so each lease binds the tenant of the scope's Operation as it stands when the lease opens. The Host's one
// pool is the data source the registration keys by this type, and disposing the Host disposes it.
internal sealed class ReadConnectionFactory(NpgsqlDataSource dataSource, Operation? operation)
    : IReadConnectionFactory
{
    public async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        // Npgsql hands out an idle pooled connection without looking at the token, so a cancelled read would still run.
        cancellationToken.ThrowIfCancellationRequested();
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var set = connection.CreateCommand();
            set.CommandText = "select set_config('pouryakit.tenant', @tenant, false)";
            set.Parameters.AddWithValue("tenant", operation?.Context?.Tenant ?? "");
            await set.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
