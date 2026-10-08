using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Persistence.Postgres;

// The connection strings the Modules' DbContexts name, read from the Host's final configuration when its container is
// built: a missing or blank one would fail the first Operation that opens its DbContext, once the Host serves. A
// DbContext the application registered itself names none here. Nothing connects.
internal sealed class PostgresConnectionsCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        return services
            .GetServices<DbContextConnection>()
            .Where(connection => connection.Name is { } name && string.IsNullOrWhiteSpace(configuration.GetConnectionString(name)))
            .GroupBy(connection => connection.Name)
            .Select(missing =>
                $"UsePostgres stores {string.Join(", ", missing.Select(connection => connection.DbContext.Name).Distinct())} over the connection string {missing.Key}, which the Host's configuration does not set (ConnectionStrings:{missing.Key}), so every Operation that opens it would fail. Set ConnectionStrings:{missing.Key}, or name in UsePostgres a connection the Host configures."
            );
    }
}
