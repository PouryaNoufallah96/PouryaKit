using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

// The connection strings the DbContexts' records name, read from the Host's final configuration when its container is
// built: a missing or blank one would fail the first Operation that opens its DbContext, once the Host serves. It lives
// where the records are made, so every name AddPouryaKitEfCore records is checked, whoever passed it. A DbContext the
// application registered itself names none here. Nothing connects.
internal sealed class DbContextConnectionsCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        return services
            .GetServices<DbContextConnection>()
            .Where(connection => connection.Name is { } name && string.IsNullOrWhiteSpace(configuration.GetConnectionString(name)))
            .GroupBy(connection => connection.Name!, StringComparer.OrdinalIgnoreCase)
            .Select(missing =>
            {
                var dbContexts = missing.Select(connection => connection.DbContext.Name).Distinct().ToList();
                var names = string.Join(", ", dbContexts);
                var (stores, isRegistered) = dbContexts.Count == 1 ? ("stores", "is") : ("store", "are");
                return $"{names} {stores} over the connection string {missing.Key}, which the Host's configuration does not set (ConnectionStrings:{missing.Key}), so every Operation that opens it would fail. Set ConnectionStrings:{missing.Key}, or name a connection the Host configures where {names} {isRegistered} registered.";
            }
            );
    }
}
