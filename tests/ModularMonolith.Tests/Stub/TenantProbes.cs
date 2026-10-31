using Dapper;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal interface ITenantProbe
{
    string Marker { get; }
}

internal sealed record TenantProbeV1(string Marker) : ITenantProbe;

internal sealed record TenantProbeSnapshotV1(string Marker) : ITenantProbe;

// Another Host's Integration Event and Snapshot, received into Catalog, a tenant-scoped Module, by the tests' own Host
// (AppFixture), so a delivery that arrives with no tenant can be shown refused there. Nothing in the Host publishes
// either: a test sends each to its subscription's queue as a fan-out from the Host's broker queue would.
internal static class TenantProbes
{
    public const string Subscription = "catalog.tenant-probes";

    public const string SnapshotSubscription = "catalog.tenant-probe-snapshots";

    public static void Receive(IServiceCollection services) =>
        services
            .Receive<TenantProbeV1>("tests.tenant-probe.v1")
            .ReceiveSnapshot<TenantProbeSnapshotV1>("tests.tenant-probe-snapshot.v1")
            .Subscribe<TenantProbeV1, TallyTenantProbeIntegrationEventHandler<TenantProbeV1>>(
                Subscription,
                typeof(Catalog.CatalogModule)
            )
            .Subscribe<
                TenantProbeSnapshotV1,
                TallyTenantProbeIntegrationEventHandler<TenantProbeSnapshotV1>
            >(SnapshotSubscription, typeof(Catalog.CatalogModule));
}

// Writes with plain SQL only, in the Unit of Work of the Module it runs in, the marker and the tenant it ran for.
internal sealed class TallyTenantProbeIntegrationEventHandler<TProbe>(
    IUnitOfWorkConnection unitOfWork,
    OperationContext context
) : IIntegrationEventHandler<TProbe>
    where TProbe : class, ITenantProbe
{
    public async Task<Result> Handle(TProbe probe, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "insert into stub.note_tallies (text, count) values (@Text, 1)",
                new { Text = $"{probe.Marker} {context.Tenant}" },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return Result.Success();
    }
}
