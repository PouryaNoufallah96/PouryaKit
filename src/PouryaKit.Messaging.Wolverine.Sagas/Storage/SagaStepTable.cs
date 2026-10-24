using System.Globalization;
using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine.Sagas;

// The claims table is created with the message store, in its schema, as the Host starts and before Wolverine does, never
// at a saga's first step: every hosted service's StartingAsync runs before any StartAsync, Wolverine's included, which
// then migrates its own tables and each declared saga's state table. Wolverine keeps a store's list of tables internal,
// so the Kit creates its own under the advisory lock Wolverine migrates the store under, where Wolverine would.
internal sealed class SagaStepTable(IServiceProvider services) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (services.GetRequiredService<IWolverineRuntime>().Options.AutoBuildMessageStorageOnStartup == AutoCreate.None)
        {
            return;
        }

        foreach (var database in services.GetServices<IMessageStore>().OfType<IMessageDatabase>().DistinctBy(database => database.Uri))
        {
            await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = string.Format(
                CultureInfo.InvariantCulture,
                SagaStepStatements.CreateTable,
                $"\"{database.SchemaName}\"",
                database.Settings.MigrationLockId
            );
            await create.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
