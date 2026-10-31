using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Marten;

// The EF store's statements (IdempotencyStatements) in a Stream Module's own key table, on its Marten Unit of Work's
// transaction, which begins before the command. Under read committed, inserting a key another transaction holds waits for
// it, then inserts nothing when it committed and the key when it rolled back. The cleanup may remove an expired key between
// the insert and the read; the key is then reserved again, so the request runs as a new one.
internal sealed class MartenIdempotencyStore(IUnitOfWork unitOfWork, TimeProvider timeProvider)
    : IIdempotencyStore
{
    private const int ReserveAttempts = 3;

    public async Task<RecordedSuccess?> ReserveAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        for (var attempt = 1; attempt <= ReserveAttempts; attempt++)
        {
            if (
                await MartenUnitOfWorkTransaction.ExecuteAsync(
                    unitOfWork,
                    IdempotencyStatements.Reserve,
                    IdempotencyStatements.Table,
                    [.. Values(key), timeProvider.GetUtcNow()],
                    cancellationToken
                ) == 1
            )
            {
                return null;
            }

            await using var read = MartenUnitOfWorkTransaction
                .Of(unitOfWork)
                .Command(IdempotencyStatements.Read, IdempotencyStatements.Table, Values(key));
            await using var recorded = await read.ExecuteReaderAsync(cancellationToken);
            if (await recorded.ReadAsync(cancellationToken))
            {
                return new RecordedSuccess(
                    await recorded.IsDBNullAsync(0, cancellationToken)
                        ? null
                        : recorded.GetString(0)
                );
            }
        }

        throw new InvalidOperationException(
            $"The key of request {key.RequestId} for {key.Command} was held, then gone before it could be read, {ReserveAttempts} times in a row; something keeps removing the keys of {IdempotencyStatements.Table}."
        );
    }

    public async Task RecordAsync(
        IdempotencyKey key,
        string response,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(response);
        if (
            await MartenUnitOfWorkTransaction.ExecuteAsync(
                unitOfWork,
                IdempotencyStatements.Record,
                IdempotencyStatements.Table,
                [.. Values(key), response],
                cancellationToken
            ) != 1
        )
        {
            throw new InvalidOperationException(
                $"The key of request {key.RequestId} for {key.Command} was not reserved in this Unit of Work's transaction, so its success cannot be recorded."
            );
        }
    }

    private static object[] Values(IdempotencyKey key) =>
        [
            key.Tenant ?? OperationContext.NoTenant,
            key.Actor.Kind.ToString(),
            key.Actor.Id,
            key.Command,
            key.RequestId,
        ];
}
