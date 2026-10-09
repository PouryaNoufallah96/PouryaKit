using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// Wolverine writes each envelope on the Unit of Work's own connection and transaction, in the store's schema, so a DbContext
// on another database than the store's writes its Outbox there: the Operation fails when that database holds no store
// tables, and commits where no delivery reads it when it holds a copy of them. The store's side is the connection string
// Wolverine was given. The DbContexts are every one AddPouryaKitEfCore made a Unit of Work, through UsePouryaKitPostgres,
// which names its connection string, or over a DbContext the application registered itself, which may name one. Each
// DbContext's side is the connection EF Core gives the DbContext itself, created in the check's own scope, whatever
// configured it, a ConfigureDbContext after UsePouryaKitPostgres included: a connection string (EF Core resolves its named
// form, Name=ConnectionStrings:app, which UsePouryaKitPostgres writes), a DbConnection or an Npgsql data source. The
// connection is not opened: nothing connects. The strings are compared on the hosts, ports and database they name; nothing
// resolves a host name, so localhost and 127.0.0.1 differ. A string that names no database names its user's, as Npgsql and
// PostgreSQL read it; one that names neither leaves the database to the client's user, which the string does not show, so
// it fails the build too, asking for the database to be named.
internal sealed class MessageStoreDatabaseCheck(string storeConnectionName, string storeConnectionString) : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        if (PostgresDatabase.Of(storeConnectionString) is not { } store)
        {
            yield return $"UsePouryaKitWolverine keeps the message store over the connection string {storeConnectionName}, which names neither a database nor a user, so its database is left to the client's user and no DbContext's can be compared with it. Set Database, or at least Username, in the connection string {storeConnectionName}.";
            yield break;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        // AddDbContext registers each DbContext scoped.
        using var scope = services.CreateScope();
        foreach (var connection in services.GetServices<DbContextConnection>())
        {
            // A missing or blank named one is DbContextConnectionsCheck's line, which AddPouryaKitEfCore registers with every record.
            if (connection.Name is { } name && string.IsNullOrWhiteSpace(configuration.GetConnectionString(name)))
            {
                continue;
            }

            var (stored, fix, together) = connection.Name is { } named
                ? (
                    $"{connection.DbContext.Name} is stored over the connection string {named}",
                    $"Set Database, or at least Username, in the connection string {named}.",
                    "Use one connection string for the message store and every Module's DbContext."
                )
                : (
                    $"AddPouryaKitEfCore makes {connection.DbContext.Name} a Unit of Work over the connection its own registration gives it",
                    $"Set Database, or at least Username, in the connection string {connection.DbContext.Name} is registered with.",
                    $"Register {connection.DbContext.Name} on the message store's database."
                );

            // Constructing the DbContext builds its model and its connection, so a model error or a named form the
            // configuration lacks fails the build as EF Core's own exception. The connection is not opened; Npgsql's provider
            // gives a DbContext with no string, connection or data source one with an empty string, which names no database.
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(connection.DbContext);
            if (PostgresDatabase.Of(context.Database.GetDbConnection().ConnectionString ?? "") is not { } database)
            {
                yield return $"{stored}, which names neither a database nor a user, so its database is left to the client's user and cannot be compared with the message store's, the database {store}. {fix}";
            }
            else if (!database.Matches(store))
            {
                yield return $"{stored}, the database {database}, but UsePouryaKitWolverine keeps the message store over the connection string {storeConnectionName}, the database {store}, so its Unit of Work would write its Outbox to its own database, where the store never delivers it. {together}";
            }
        }
    }

    // A database is its servers, each a host and a port, in order, and its name.
    private sealed record PostgresDatabase(IReadOnlyList<(string Host, int Port)> Servers, string Name)
    {
        // Null when the string names neither a database nor a user.
        public static PostgresDatabase? Of(string connectionString)
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString);
            return (settings.Database ?? settings.Username) is { } name
                ? new(
                    [
                        .. (settings.Host ?? "")
                            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Select(host => Server(host, settings.Port))
                            .OrderBy(server => server.Host, StringComparer.Ordinal)
                            .ThenBy(server => server.Port),
                    ],
                    name
                )
                : null;
        }

        public bool Matches(PostgresDatabase other) => Servers.SequenceEqual(other.Servers) && Name == other.Name;

        public override string ToString() => $"{string.Join(',', Servers.Select(server => $"{server.Host}:{server.Port}"))}/{Name}";

        // Npgsql's reading of one host (NpgsqlConnectionStringBuilder.IsUnixSocket, then TrySplitHostPort). A rooted path, or
        // one starting with @, is the directory of a Unix socket, whose name the separate port completes: its case is kept and
        // no port is split off. Any other host ignores case, and its last colon starts a port, parsed as a number, unless the
        // host is a bare IPv6 address; without one it takes the separate port.
        private static (string Host, int Port) Server(string host, int port)
        {
            if (Path.IsPathRooted(host) || host.StartsWith('@'))
            {
                return (host, port);
            }

            var colon = host.LastIndexOf(':');
            var previous = colon == -1 ? -1 : host.AsSpan(0, colon).LastIndexOf(':');
            var bracket = host.LastIndexOf(']');
            return
                colon != -1
                && (previous == -1 || (colon > bracket && previous < bracket))
                && int.TryParse(host.AsSpan(colon + 1), out var written)
                ? (host[..colon].ToLowerInvariant(), written)
                : (host.ToLowerInvariant(), port);
        }
    }
}
