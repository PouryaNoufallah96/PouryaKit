using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PouryaKit.Cqrs;
using Weasel.Postgresql;

namespace M2.Marten;

// The Marten side of the Kit's MessageStoreDatabaseCheck: Wolverine's Marten outbox writes each envelope on the Marten
// session's connection, into the message store's tables, so a store on another database writes where no delivery reads.
// The store's side is the data source Marten itself built, read from the built store without connecting.
internal sealed class MartenStoreDatabaseCheck(string storeConnectionName, string storeConnectionString) : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var marten = services.GetRequiredService<MartenConnection>();
        var database = (PostgresqlDatabase)services.GetRequiredService<IDocumentStore>().Storage.Database;
        var martens = new NpgsqlConnectionStringBuilder(database.DataSource.ConnectionString);
        var store = new NpgsqlConnectionStringBuilder(storeConnectionString);
        if (martens.Host != store.Host || martens.Port != store.Port || martens.Database != store.Database)
        {
            yield return $"Marten's store is the database {martens.Database} of the connection string {marten.Name}, but the message store is the database {store.Database} of {storeConnectionName}, so Wolverine's Marten outbox would write envelopes where the store never delivers them. Use one database for both.";
        }
    }
}
