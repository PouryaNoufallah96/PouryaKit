using Catalog.Contracts.Products.Events.V1;
using Dapper;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace Inventory.CatalogProducts.Features.CopyProduct;

// Keeps Inventory's copy of each catalog product current, in a table of its own that is no aggregate: it holds what
// Catalog last published and nothing Inventory decides. Only the effect: the Kit runs this only for a Snapshot newer
// than every one applied of that product, so a late, duplicated or concurrent older one never arrives here, and the
// copy simply follows each one, deleted on the Tombstone.
internal sealed class CopyProductIntegrationEventHandler(IUnitOfWorkConnection unitOfWork)
    : IIntegrationEventHandler<ProductSnapshotV1>
{
    public async Task<Result> Handle(ProductSnapshotV1 snapshot, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        var sql = snapshot.IsRemoved
            ? "delete from inventory.catalog_products where product_id = @ProductId"
            : """
              insert into inventory.catalog_products (product_id, name, version) values (@ProductId, @Name, @Version)
              on conflict (product_id) do update set name = excluded.name, version = excluded.version
              """;
        await connection.ExecuteAsync(new CommandDefinition(sql, snapshot, transaction, cancellationToken: cancellationToken));
        return Result.Success();
    }
}
