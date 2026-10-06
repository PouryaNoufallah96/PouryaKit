using Catalog.Products.Features.RemoveProducts;
using ModularMonolith.Host;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Security;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// The Host's own source, plus an explicit grant to the tests' named Actor: the Gateway tests run as
// GatewayTestExtensions.TestActor, independent work with no HTTP caller, which holds only the permissions granted here,
// as a named system Actor of the Host would.
internal sealed class TestActorPermissions(CallerPermissions callers) : IActorPermissions
{
    public static IReadOnlyList<string> Granted { get; } = [RemoveProductsCommand.Permission, ReadStampLedgerQuery.Permission];

    public Task<bool> HasPermissionAsync(OperationContext context, string permission, CancellationToken cancellationToken) =>
        context.Actor == GatewayTestExtensions.TestActor
            ? Task.FromResult(Granted.Contains(permission))
            : callers.HasPermissionAsync(context, permission, cancellationToken);
}
