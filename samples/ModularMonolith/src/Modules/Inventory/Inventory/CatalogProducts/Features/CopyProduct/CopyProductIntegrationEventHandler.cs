using Catalog.Contracts.Products.Events.V1;
using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace Inventory.CatalogProducts.Features.CopyProduct;

#region copy-product
// Keeps Inventory's copy of each catalog product current, in a table of its own that is no aggregate: it holds what
// Catalog last published and nothing Inventory decides, keyed by tenant and product under the tenant the Snapshot was
// delivered for. Only the effect: the Kit runs this only for a Snapshot newer than every one applied of that product,
// so a late, duplicated or concurrent older one never arrives here, and the copy simply follows each one, deleted on the
// Tombstone.
internal sealed class CopyProductIntegrationEventHandler(
    IUnitOfWorkConnection unitOfWork,
    OperationContext context
) : IIntegrationEventHandler<ProductSnapshotV1>
{
    public async Task<Result> Handle(
        ProductSnapshotV1 snapshot,
        CancellationToken cancellationToken
    )
    {
        var (connection, transaction) = unitOfWork.Join();
        var sql = snapshot.IsRemoved
            ? "delete from inventory.catalog_products where tenant = @Tenant and product_id = @ProductId"
            : """
                insert into inventory.catalog_products (tenant, product_id, name, version) values (@Tenant, @ProductId, @Name, @Version)
                on conflict (tenant, product_id) do update set name = excluded.name, version = excluded.version
                """;
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    context.Tenant,
                    snapshot.ProductId,
                    snapshot.Name,
                    snapshot.Version,
                },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return Result.Success();
    }
}
#endregion
