using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;
using Wolverine.Persistence.Sagas;
using Wolverine.RDBMS;
using Wolverine.RDBMS.Sagas;
using Wolverine.Runtime;

namespace PouryaKit.Messaging.Wolverine.Sagas;

/// <summary>
/// What a declared Saga's generated chain calls, and how the Kit derives a saga's identities: Wolverine's lightweight
/// saga storage in the message store's database and schema, on a transaction the Kit owns and claims the step in, before
/// Wolverine loads the saga (ADR 0013). Feature code never calls it.
/// </summary>
public static class SagaStorage
{
    private static readonly ConditionalWeakTable<IMessageDatabase, ConcurrentDictionary<Type, object>> Schemas = [];

    /// <summary>
    /// Opens a connection and a transaction on the message store's database, refuses an Integration Event with no tenant,
    /// claims the step, enlists <paramref name="context"/> in the transaction, so the step's emissions and timeouts are
    /// written there, and returns the saga's storage on it, with the signature of Wolverine's own helper. A timeout's
    /// claim takes the identity its saga's state kept for it, read here in the transaction; a timeout whose saga is gone
    /// claims nothing, and Wolverine's chain then ends.
    /// </summary>
    public static async ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(
        MessageContext context,
        Func<SagaTableDefinition, DatabaseSettings, IDatabaseSagaSchema<TId, TSaga>> factory
    )
        where TSaga : Saga
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(factory);
        var envelope = context.Envelope!;
        var saga = context.Runtime.Services.GetRequiredService<SagaDeclarations>().Of(typeof(TSaga));
        if (envelope.Message is ISagaInput { Tenant: null } tenantless)
        {
            throw new SagaInputRefusedException(
                saga.Name,
                $"The Integration Event {tenantless.WireName} {tenantless.EventId} reached the Saga {saga.Name} with no tenant, so it names no saga: a saga's identity is its tenant with its business id."
            );
        }

        var database =
            context.Storage as IMessageDatabase
            ?? throw new InvalidOperationException(
                $"The Saga {saga.Name} keeps its state in the Host's message store, but the store is {context.Storage.GetType().Name}, not a PostgreSQL database. Call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore, or UsePouryaKitMarten."
            );
        var connection = (NpgsqlConnection)await database.DataSource.OpenConnectionAsync();
        try
        {
            var transaction = await connection.BeginTransactionAsync();
            var schema = (IDatabaseSagaSchema<TId, TSaga>)
                Schemas.GetOrCreateValue(database).GetOrAdd(typeof(TSaga), _ => factory(new SagaTableDefinition(typeof(TSaga), null), database.Settings));
            var input = envelope.Message is ISagaInput delivered
                ? Input(saga, delivered.WireName, delivered.EventId, delivered.SagaId)
                : await schema.LoadAsync((TId)(object)envelope.SagaId!, transaction, CancellationToken.None) is PouryaKitSaga scheduler
                    ? TimeoutInput(saga, envelope, scheduler)
                    : (SagaStepInput?)null;
            var claimed = input is not { } step || await ClaimAsync(connection, transaction, database.SchemaName, saga.Name, step);
            await context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(database, transaction));
            return new SagaStepStorage<TId, TSaga>(
                new DatabaseSagaStorage<TId, TSaga>(connection, transaction, schema),
                new SagaStep(saga, context, input),
                alreadyApplied: !claimed
            );
        }
        catch (Exception)
        {
            await connection.CloseAsync();
            throw;
        }
    }

    /// <summary>
    /// Makes the step <paramref name="storage"/> was opened for the running one, whose emissions
    /// <see cref="PouryaKitSaga"/> writes, notes whether its input may start the saga, and returns true when its claim
    /// was another's: the step was applied, and the generated chain ends, which rolls its transaction back.
    /// </summary>
    public static bool Enter(object storage, bool startsSaga)
    {
        var step = (ISagaStepStorage)storage;
        step.Enter(startsSaga);
        return step.AlreadyApplied;
    }

    /// <summary>
    /// A saga's identity: the event's tenant and its business id, each escaped as a URI's data, so neither can end its
    /// part early and name another tenant's saga.
    /// </summary>
    public static string SagaId(string? tenant, string businessId)
    {
        ArgumentNullException.ThrowIfNull(businessId);
        return $"{Uri.EscapeDataString(tenant ?? "")}:{Uri.EscapeDataString(businessId)}";
    }

    /// <summary>
    /// A step's identity, the causation of what it emits: a name-based UUID (RFC 9562 version 8) of the saga's name, its
    /// identity, the input's wire name and the input's identity, so every delivery of one input repeats it.
    /// </summary>
    public static Guid StepId(string saga, string sagaId, string inputWireName, Guid inputId) =>
        NameBased($"{saga}\n{sagaId}\n{inputWireName}\n{inputId:D}");

    /// <summary>The identity of what a step emits of one type: from the step's identity and the emission's wire name.</summary>
    public static Guid EmissionId(Guid stepId, string wireName) => NameBased($"{stepId:D}\n{wireName}");

    /// <summary>
    /// The identity of a timeout a step schedules: from the scheduling step's identity, the timeout's wire name and its
    /// <paramref name="ordinal"/>, its place from 0 among the step's timeouts of that type in the order the step
    /// scheduled them, so two timeouts of one type from one step are two.
    /// </summary>
    public static Guid TimeoutId(Guid schedulingStepId, string timeoutWireName, int ordinal) =>
        NameBased($"{schedulingStepId:D}\n{timeoutWireName}\n{ordinal.ToString(CultureInfo.InvariantCulture)}\ntimeout");

    private static SagaStepInput Input(SagaDeclaration saga, string wireName, Guid id, string sagaId) =>
        new(wireName, id, sagaId, StepId(saga.Name, sagaId, wireName, id));

    // A timeout's identity is the one its saga's state kept when the step that scheduled it stored the saga.
    private static SagaStepInput TimeoutInput(SagaDeclaration saga, Envelope envelope, PouryaKitSaga scheduler) =>
        scheduler.Timeouts.TryGetValue(envelope.Id, out var timeout)
            ? Input(saga, envelope.MessageType!, timeout, envelope.SagaId!)
            : throw new InvalidOperationException(
                $"The timeout {envelope.Id} of the Saga {saga.Name} has no identity in the state of the saga {envelope.SagaId}: only a step the Kit stores schedules a timeout."
            );

    // On conflict the insert waits for a concurrent claim of the same key to commit or roll back: committed, it inserts
    // nothing and the step is skipped; rolled back, it inserts and the step runs. A claim rolls back with its step, so a
    // step that dies before its commit is claimed again by its redelivery.
    private static async Task<bool> ClaimAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string schema, string saga, SagaStepInput step)
    {
        await using var claim = new NpgsqlCommand(
            string.Format(CultureInfo.InvariantCulture, SagaStepStatements.Claim($"\"{schema}\".{SagaStepStatements.Table}"), "$1", "$2", "$3", "$4"),
            connection,
            transaction
        )
        {
            Parameters = { new() { Value = saga }, new() { Value = step.WireName }, new() { Value = step.Id }, new() { Value = step.SagaId } },
        };
        return await claim.ExecuteNonQueryAsync() == 1;
    }

    private static Guid NameBased(string text)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
