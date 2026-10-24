using Catalog;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Testing;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// Catalog's Gateway for a test that does not care who runs its Operation. Catalog is tenant-scoped, so these take the
/// place of <see cref="GatewayTestExtensions"/>' calls for Catalog, which run with no tenant: each runs as a new
/// Operation of <see cref="GatewayTestExtensions.TestActor"/> for <see cref="AppFixture.TestTenant"/>. A test that needs
/// another Actor or tenant, or none, passes its own <see cref="OperationContext"/> to the Gateway's own methods.
/// </summary>
public static class CatalogGatewayExtensions
{
    extension(IGatewayProcessor<CatalogModule> catalog)
    {
        /// <summary>Runs <paramref name="command"/> through Catalog's Gateway for the test tenant.</summary>
        public Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default) =>
            catalog.SendAsync(command, TestContext(), cancellationToken);

        /// <summary>Runs <paramref name="command"/> through Catalog's Gateway for the test tenant.</summary>
        public Task<Result<TResponse>> SendAsync<TResponse>(
            Command<TResponse> command,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull => catalog.SendAsync(command, TestContext(), cancellationToken);

        /// <summary>Runs <paramref name="query"/> through Catalog's Gateway for the test tenant.</summary>
        public Task<Result<TResponse>> QueryAsync<TResponse>(
            Query<TResponse> query,
            CancellationToken cancellationToken = default
        )
            where TResponse : notnull => catalog.QueryAsync(query, TestContext(), cancellationToken);
    }

    private static OperationContext TestContext() => new(GatewayTestExtensions.TestActor, AppFixture.TestTenant);
}
