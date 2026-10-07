using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Dapper;

// The connection string the Host's Dapper reads name, read from the Host's final configuration when it starts: a missing
// or blank one would fail the first read, once the Host serves. Nothing connects.
internal sealed class ReadConnectionCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services) =>
        services.GetService<ReadConnection>() is { } connection
        && string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>().GetConnectionString(connection.Name))
            ? [
                $"Dapper reads use the connection string {connection.Name}, which the Host's configuration does not set (ConnectionStrings:{connection.Name}), so every read would fail. Set ConnectionStrings:{connection.Name}, or name in AddPouryaKitDapper a connection the Host configures.",
            ]
            : [];
}
