using System.Data.Common;
using Npgsql;

namespace PouryaKit.Persistence.Dapper;

// One per Host: its data source is the connection pool every Module's reads lease from, and disposing the Host
// disposes the pool.
internal sealed class ReadConnectionFactory(string connectionString) : IReadConnectionFactory, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        // Npgsql hands out an idle pooled connection without looking at the token, so a cancelled read would still run.
        cancellationToken.ThrowIfCancellationRequested();
        return await _dataSource.OpenConnectionAsync(cancellationToken);
    }

    public void Dispose() => _dataSource.Dispose();

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
