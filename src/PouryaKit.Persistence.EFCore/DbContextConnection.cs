namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// A DbContext that is a Unit of Work, <see cref="DbContext"/>, and the name of the connection string in the Host's
/// configuration it stores over, <see cref="Name"/>, when its registration names one: <c>AddPouryaKitEfCore</c> records one
/// for each DbContext it makes a Unit of Work, with the name a database package such as <c>UsePostgres</c> passes, and
/// none for a DbContext the application registered itself. The Kit's composition checks read them when the Host's
/// container is built: that the configuration sets each named one, and that every one's connection names the message
/// store's database. Applications and Feature code never register one.
/// </summary>
public sealed record DbContextConnection(string? Name, Type DbContext);
