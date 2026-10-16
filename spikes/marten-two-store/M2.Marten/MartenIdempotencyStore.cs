using PouryaKit.Cqrs;

namespace M2.Marten;

// EfIdempotencyStore's statements on the Marten Unit of Work's session and transaction: a key another transaction holds
// makes the reservation wait for it, then insert nothing when it committed and the key when it rolled back. The cleanup
// may remove an expired key between the insert and the read; the key is then reserved again.
internal sealed class MartenIdempotencyStore(IUnitOfWork unitOfWork, TimeProvider timeProvider, string schema) : IIdempotencyStore
{
    private const int ReserveAttempts = 3;
    private const string Key = "tenant = ? and actor_kind = ? and actor_id = ? and command = ? and request_id = ?";

    private string Table => $"{schema}.{MartenTables.IdempotencyKeys}";

    public async Task<RecordedSuccess?> ReserveAsync(IdempotencyKey key, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ReserveAttempts; attempt++)
        {
            var session = Session();
            var reserved = await session.AdvancedSql.QueryAsync<int>(
                $"insert into {Table} (tenant, actor_kind, actor_id, command, request_id, reserved_at) values (?, ?, ?, ?, ?, ?) on conflict do nothing returning 1",
                cancellationToken,
                [.. Values(key), timeProvider.GetUtcNow()]
            );
            if (reserved.Count == 1)
            {
                return null;
            }

            var recorded = await session.AdvancedSql.QueryAsync<string?>($"select response from {Table} where {Key}", cancellationToken, Values(key));
            if (recorded is [var response])
            {
                return new RecordedSuccess(response);
            }
        }

        throw new InvalidOperationException($"The key of request {key.RequestId} for {key.Command} was held, then gone before it could be read, {ReserveAttempts} times in a row.");
    }

    public async Task RecordAsync(IdempotencyKey key, string response, CancellationToken cancellationToken)
    {
        var recorded = await Session().AdvancedSql.QueryAsync<int>(
            $"update {Table} set response = ? where {Key} returning 1",
            cancellationToken,
            [response, .. Values(key)]
        );
        if (recorded.Count != 1)
        {
            throw new InvalidOperationException($"The key of request {key.RequestId} for {key.Command} was not reserved in this Unit of Work's transaction.");
        }
    }

    private static object[] Values(IdempotencyKey key) =>
        [key.Tenant ?? OperationContext.NoTenant, key.Actor.Kind.ToString(), key.Actor.Id, key.Command, key.RequestId];

    private global::Marten.IDocumentSession Session() =>
        (unitOfWork as MartenUnitOfWork
            ?? throw new InvalidOperationException($"Marten idempotency keys are kept in a Marten Unit of Work, but the scope's is {unitOfWork.GetType().Name}.")
        ).ActiveSession;
}
