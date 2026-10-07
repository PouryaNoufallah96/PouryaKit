namespace PouryaKit.Cqrs;

/// <summary>
/// Where the dispatcher keeps the keys of <see cref="IdempotentAttribute"/> commands: in the transaction of the Unit of
/// Work the command runs in, so a key and its recorded success commit with the command's effect or not at all. A
/// persistence package registers it, scoped, as <c>PouryaKit.Persistence.EFCore</c> does with a table in each Module's
/// own schema. Feature code never calls it.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Reserves <paramref name="key"/> in the active Unit of Work's transaction and returns null; when a committed
    /// Operation holds it, returns the success that Operation recorded instead. While the uncommitted transaction of
    /// another Operation holds it, waits until that transaction ends.
    /// </summary>
    Task<RecordedSuccess?> ReserveAsync(IdempotencyKey key, CancellationToken cancellationToken);

    /// <summary>Records <paramref name="response"/> for <paramref name="key"/>, reserved in the same transaction.</summary>
    Task RecordAsync(IdempotencyKey key, string response, CancellationToken cancellationToken);
}

/// <summary>
/// The key of one idempotent request: the tenant, null where the Operation has none; the Actor; the command's full type
/// name; and the request id.
/// </summary>
public sealed record IdempotencyKey(string? Tenant, Actor Actor, string Command, string RequestId);

/// <summary>
/// The success a committed Operation recorded for a key: its Result's value in the Kit's JSON representation, or null
/// for a command without a value.
/// </summary>
public sealed record RecordedSuccess(string? Response);
