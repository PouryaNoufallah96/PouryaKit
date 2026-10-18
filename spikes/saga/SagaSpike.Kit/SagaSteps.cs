using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;
using Wolverine.Postgresql.Internals;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace SagaSpike.Kit;

// Each declared saga's chain opens its storage through the Kit, first in its middleware, in place of the storage frame
// Wolverine's transactional policy may already have added, and so that none is added later
// (LightweightSagaPersistenceFrameProvider.ApplyTransactionSupport returns when the chain has one); the Kit's guard runs
// next, inside the saga's transaction and before Wolverine loads the saga. The frame is Wolverine's own; only the helper it
// calls is the Kit's (SagaSchemaCodegen.HelperType).
internal sealed class SagaStepPolicy(SagaDeclarations sagas) : IHandlerPolicy
{
    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.OfType<SagaChain>().Where(chain => sagas.Declares(chain.SagaType)))
        {
            chain.Middleware.RemoveAll(frame => frame is EnrollAndFetchSagaStorageFrame);
            var codegen = new SagaSchemaCodegen(typeof(DatabaseSagaSchema<,>).MakeGenericType(chain.SagaType, typeof(string)), typeof(SagaStorage));
            chain.Middleware.Insert(0, new EnrollAndFetchSagaStorageFrame(typeof(string), chain.SagaType, codegen));
            chain.Middleware.Insert(1, new SkipClaimedStepFrame(chain.SagaType));
        }
    }
}

// if (SagaStorage.IsClaimed(storage)) return; — leaving before the commit disposes the storage, which rolls the step's
// transaction back: no state, no emission, and the envelope is marked handled.
internal sealed class SkipClaimedStepFrame(Type sagaType) : SyncFrame
{
    private Variable? _storage;

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _storage = chain.FindVariable(typeof(ISagaStorage<,>).MakeGenericType(typeof(string), sagaType));
        yield return _storage;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"if ({typeof(SagaStorage).FullNameInCode()}.{nameof(SagaStorage.IsClaimed)}({_storage!.Usage})) return;");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// What a declared saga's generated chain calls to open its storage: Wolverine's lightweight saga storage in the message
/// store's database and schema, on a transaction the Kit owns, in which it first claims the step.
/// </summary>
public static class SagaStorage
{
    private static readonly ConditionalWeakTable<IMessageDatabase, ConcurrentDictionary<Type, object>> Schemas = [];
    private static readonly ConditionalWeakTable<IMessageDatabase, object> ClaimTables = [];

    /// <summary>
    /// Opens a connection and a transaction on the message store's database, enlists <paramref name="context"/> in it, so
    /// the step's emissions and timeouts are written there, refuses a tenantless Integration Event, claims the step, and
    /// returns the saga's storage on that transaction. The same signature as Wolverine's <c>RelationalSagaStorage</c>.
    /// </summary>
    public static async ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(
        MessageContext context,
        Func<SagaTableDefinition, DatabaseSettings, IDatabaseSagaSchema<TId, TSaga>> factory
    )
        where TSaga : Saga
    {
        var envelope = context.Envelope!;
        var services = context.Runtime.Services;
        var saga = services.GetRequiredService<SagaDeclarations>().Of(typeof(TSaga));
        var options = services.GetRequiredService<SagaSpikeOptions>();
        // Refused before any step: a tenant-scoped saga's identity has no tenant to hold.
        if (envelope.Message is ISagaInput { Tenant: null } tenantless)
        {
            throw new InvalidOperationException(
                $"Tenant.Required: {tenantless.WireName} {tenantless.EventId} reached the saga {saga.Name} with no tenant, so it has no saga identity."
            );
        }

        var database = (IMessageDatabase)context.Storage;
        await EnsureClaimTableAsync(database);
        var connection = (NpgsqlConnection)await database.DataSource.OpenConnectionAsync();
        try
        {
            var transaction = await connection.BeginTransactionAsync();
            var claimed = await ClaimAsync(connection, transaction, database.SchemaName, saga.Name, envelope, options.ClaimKey);
            await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, transaction));
            var settings = options.StoreSettings
                ? database.Settings
                : new DatabaseSettings { SchemaName = "public", DataSource = database.Settings.DataSource, ConnectionString = database.Settings.ConnectionString };
            var schema = (IDatabaseSagaSchema<TId, TSaga>)
                Schemas.GetOrCreateValue(database).GetOrAdd(typeof(TSaga), _ => factory(new SagaTableDefinition(typeof(TSaga), null), settings));
            return new SagaStepStorage<TId, TSaga>(
                new DatabaseSagaStorage<TId, TSaga>(connection, transaction, schema),
                transaction,
                database.SchemaName,
                saga.Name,
                envelope,
                !claimed,
                options
            );
        }
        catch (Exception)
        {
            await connection.CloseAsync();
            throw;
        }
    }

    /// <summary>Whether the step <paramref name="storage"/> was opened for was already applied: its claim is another's.</summary>
    public static bool IsClaimed(object storage) => storage is ISagaStep { Claimed: true };

    /// <summary>
    /// The step's identity, the causation of what it emits: a name-based UUID (RFC 9562 version 8) of the saga's name, the
    /// saga's identity, the input's wire name and the input's identity, so every delivery of one input repeats it.
    /// </summary>
    public static Guid StepId(string saga, string sagaId, string inputWireName, Guid inputId) =>
        NameBased($"{saga}\n{sagaId}\n{inputWireName}\n{inputId:D}");

    /// <summary>The identity of what a step emits of one type: from the step's identity and the emission's wire name.</summary>
    public static Guid EmissionId(Guid stepId, string wireName) => NameBased($"{stepId:D}\n{wireName}");

    /// <summary>The input a delivery carries: an Integration Event's wire name and identity, or a timeout's type name and envelope id.</summary>
    public static (string WireName, Guid Id, string SagaId) InputOf(Envelope envelope) =>
        envelope.Message is ISagaInput input
            ? (input.WireName, input.EventId, input.SagaId)
            : (envelope.MessageType!, envelope.Id, envelope.SagaId!);

    private static Guid NameBased(string text)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    // On conflict the insert waits for a concurrent claim of the same key to commit or roll back: committed, it inserts
    // nothing and the step is skipped; rolled back, it inserts and the step runs.
    private static async Task<bool> ClaimAsync(
        NpgsqlConnection connection,
        DbTransaction transaction,
        string schema,
        string saga,
        Envelope envelope,
        SagaClaimKey key
    )
    {
        if (key == SagaClaimKey.None)
        {
            return true;
        }

        var (wireName, id, sagaId) = InputOf(envelope);
        await using var claim = new NpgsqlCommand(
            $"insert into \"{schema}\".pouryakit_saga_steps (saga, input, event_id, saga_id) values (@saga, @input, @event, @saga_id) on conflict do nothing",
            connection,
            (NpgsqlTransaction)transaction
        );
        claim.Parameters.AddWithValue("saga", saga);
        claim.Parameters.AddWithValue("input", key == SagaClaimKey.WithoutWireName ? "" : wireName);
        claim.Parameters.AddWithValue("event", key == SagaClaimKey.EnvelopeId ? envelope.Id : id);
        claim.Parameters.AddWithValue("saga_id", sagaId);
        return await claim.ExecuteNonQueryAsync() == 1;
    }

    // In the message store's schema, beside the saga's own table, created once per store as Wolverine creates the saga's.
    private static async Task EnsureClaimTableAsync(IMessageDatabase database)
    {
        if (ClaimTables.TryGetValue(database, out _))
        {
            return;
        }

        await using var connection = (NpgsqlConnection)await database.DataSource.OpenConnectionAsync();
        await using var create = new NpgsqlCommand(
            $"""
            create schema if not exists "{database.SchemaName}";
            create table if not exists "{database.SchemaName}".pouryakit_saga_steps (
                saga text not null, input text not null, event_id uuid not null, saga_id text not null,
                claimed_at timestamp with time zone not null default now(), primary key (saga, input, event_id));
            """,
            connection
        );
        await create.ExecuteNonQueryAsync();
        ClaimTables.AddOrUpdate(database, new object());
    }
}

internal interface ISagaStep
{
    bool Claimed { get; }
}

// Wolverine's storage on the Kit's transaction: an inserted saga takes its identity, tenant and correlation from the
// event that started it, so the saga's own Start code never sets them.
internal sealed class SagaStepStorage<TId, TSaga>(
    DatabaseSagaStorage<TId, TSaga> inner,
    DbTransaction transaction,
    string schema,
    string saga,
    Envelope envelope,
    bool claimed,
    SagaSpikeOptions options
) : ISagaStorage<TId, TSaga>, ISagaStep
    where TSaga : Saga
{
    public bool Claimed { get; } = claimed;

    public Task<TSaga?> LoadAsync(TId id, CancellationToken cancellationToken) => inner.LoadAsync(id, cancellationToken);

    public Task InsertAsync(TSaga state, CancellationToken cancellationToken)
    {
        if (state is PouryaKitSaga kit && envelope.Message is ISagaInput input)
        {
            kit.Id = input.SagaId;
            kit.Tenant = input.Tenant;
            kit.Correlation = input.Correlation;
        }

        return inner.InsertAsync(state, cancellationToken);
    }

    public Task UpdateAsync(TSaga state, CancellationToken cancellationToken) => inner.UpdateAsync(state, cancellationToken);

    public async Task DeleteAsync(TSaga state, CancellationToken cancellationToken)
    {
        if (options.DeleteClaimsWithState && state is PouryaKitSaga kit)
        {
            await using var delete = new NpgsqlCommand(
                $"delete from \"{schema}\".pouryakit_saga_steps where saga = @saga and saga_id = @saga_id",
                (NpgsqlConnection)transaction.Connection!,
                (NpgsqlTransaction)transaction
            );
            delete.Parameters.AddWithValue("saga", saga);
            delete.Parameters.AddWithValue("saga_id", kit.Id);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await inner.DeleteAsync(state, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await inner.SaveChangesAsync(cancellationToken);
        if (options.AfterCommit is { } afterCommit)
        {
            var (wireName, id, _) = SagaStorage.InputOf(envelope);
            await afterCommit(wireName, id);
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
