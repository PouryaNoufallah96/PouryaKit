namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// A connection string, by its name in the Host's configuration, on which a store package other than EF Core's writes
/// what its Unit of Work's Operations publish into the message store's tables, as Wolverine's Marten outbox writes on the
/// Marten store's connection. The store package records one for its store, and the message store's database check of
/// <c>UsePouryaKitWolverine(connectionName)</c> compares its database with the store's when the Host is built, as it
/// compares each Module's DbContext; <see cref="Writer"/> names the store in the check's message. Applications never
/// register one.
/// </summary>
public sealed record OutboxConnection(string Name, string Writer);
