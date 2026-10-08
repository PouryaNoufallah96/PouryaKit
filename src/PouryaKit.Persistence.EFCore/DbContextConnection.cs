namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The connection string, named <see cref="Name"/> in the Host's configuration, that <see cref="DbContext"/> stores over.
/// A database package's registration, such as <c>UsePostgres</c>, records one for each call, and the Kit's composition
/// checks read them when the Host's container is built: that the configuration sets each one, and that each names the
/// message store's database. Applications and Feature code never register one.
/// </summary>
public sealed record DbContextConnection(string Name, Type DbContext);
