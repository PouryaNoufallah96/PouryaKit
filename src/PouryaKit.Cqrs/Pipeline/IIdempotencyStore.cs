namespace PouryaKit.Cqrs;

/// <summary>
/// Where the dispatcher keeps the keys of <see cref="IdempotentAttribute"/> commands: in the transaction of the Unit of
/// Work the command runs in, so a key and its recorded success commit with the command's effect or not at all. A
/// persistence package registers it, scoped, as <c>PouryaKit.Persistence.EFCore</c> does with a table in each Module's
/// own schema, keyed by the Module on a Module's builder, which a Host of Modules routes to by the Operation's Module
/// (<see cref="IModuleServices.RouteByModule{TService}"/>). Feature code never calls it.
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
