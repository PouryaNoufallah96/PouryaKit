namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// A store other than EF Core's whose Unit of Work writes what its Operations publish into the message store's tables on a
/// connection of its own, as Wolverine's Marten outbox writes on the Marten store's: the store package records one for its
/// store, and the message store's database check of <c>UsePouryaKitWolverine(connectionName)</c> compares it with the store
/// when the Host is built, as it compares each Module's DbContext. <see cref="ConnectionString"/> and <see cref="Schema"/>
/// read, from the built Host's services and without connecting, the connection string the store was built with and the
/// schema it keeps the message store's tables in, whatever configured them; <see cref="Writer"/> names the store in the
/// check's messages. Applications never register one.
/// </summary>
public sealed record OutboxConnection(string Writer, Func<IServiceProvider, string> ConnectionString, Func<IServiceProvider, string?> Schema);
