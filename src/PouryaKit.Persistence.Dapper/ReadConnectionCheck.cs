using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Dapper;

// The connection string the Host's Dapper reads name, read from the Host's final configuration when its container is
// built: a missing or blank one would fail the first read, once the Host serves, and a multiplexed one would not keep the
// tenant a lease set. Nothing connects.
internal sealed class ReadConnectionCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        if (services.GetService<ReadConnection>() is not { } connection)
        {
            return [];
        }

        var connectionString = services.GetRequiredService<IConfiguration>().GetConnectionString(connection.Name);
        return string.IsNullOrWhiteSpace(connectionString)
            ? [
                $"Dapper reads use the connection string {connection.Name}, which the Host's configuration does not set (ConnectionStrings:{connection.Name}), so every read would fail. Set ConnectionStrings:{connection.Name}, or name in AddPouryaKitDapper a connection the Host configures.",
            ]
            : connection.Refusal(connectionString) is { } refusal ? [refusal]
            : [];
    }
}
