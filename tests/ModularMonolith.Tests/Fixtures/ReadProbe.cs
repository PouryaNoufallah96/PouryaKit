using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Persistence.Dapper;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// A stop point after a Dapper read of the test's own flow has run its query and before its handler returns: the
/// disposal of the connection the handler read on waits until the test releases it. Every other read runs as usual.
/// </summary>
internal static class ReadProbe
{
    private static readonly AsyncLocal<Hold?> Armed = new();

    /// <summary>Holds the Dapper reads of the calling flow, the first at its connection's disposal, until released.</summary>
    public static void HoldAfterRead(Hold hold) => Armed.Value = hold;

    /// <summary>Wraps the Host's Dapper connection factory so its connections pass through the probe.</summary>
    public static void Install(IServiceCollection services)
    {
        var reads = services.Single(descriptor => descriptor.ServiceType == typeof(IReadConnectionFactory));
        services.Remove(reads);
        services.AddSingleton<IReadConnectionFactory>(provider => new HeldReads((IReadConnectionFactory)reads.ImplementationFactory!(provider)));
    }

    // Disposed with the Host in the wrapped factory's place, so the Host's disposal still reaches its connection pool.
    private sealed class HeldReads(IReadConnectionFactory reads) : IReadConnectionFactory, IDisposable, IAsyncDisposable
    {
        public async ValueTask<DbConnection> OpenAsync(CancellationToken cancellationToken = default)
        {
            var connection = await reads.OpenAsync(cancellationToken);
            return Armed.Value is { } hold ? new HeldConnection(connection, hold) : connection;
        }

        public void Dispose() => (reads as IDisposable)?.Dispose();

        public ValueTask DisposeAsync() => reads is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }

    private sealed class HeldConnection(DbConnection connection, Hold hold) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString
        {
            get => connection.ConnectionString;
            set => connection.ConnectionString = value;
        }

        public override string Database => connection.Database;

        public override string DataSource => connection.DataSource;

        public override string ServerVersion => connection.ServerVersion;

        public override ConnectionState State => connection.State;

        public override void ChangeDatabase(string databaseName) => connection.ChangeDatabase(databaseName);

        public override void Close() => connection.Close();

        public override void Open() => connection.Open();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => connection.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => connection.CreateCommand();

        public override async ValueTask DisposeAsync()
        {
            if (hold.Changed.TrySetResult())
            {
                await hold.Released.Task;
            }

            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                connection.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
