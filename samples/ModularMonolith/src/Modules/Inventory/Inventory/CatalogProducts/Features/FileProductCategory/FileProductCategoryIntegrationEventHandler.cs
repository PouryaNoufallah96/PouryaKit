using Catalog.Contracts.Products.Events.V2;
using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace Inventory.CatalogProducts.Features.FileProductCategory;

// Files each new product under its catalog category, which only the second version of ProductCreated carries. It
// subscribes to ProductCreatedV2 alone, beside the v1 subscription that opens the stock item, and receives no v1: each
// subscription gets only the version it declared. Only the effect: the Kit records the Receipt and commits, so a
// redelivered ProductCreatedV2 files the product once.
internal sealed class FileProductCategoryIntegrationEventHandler(IUnitOfWorkConnection unitOfWork, OperationContext context)
    : IIntegrationEventHandler<ProductCreatedV2>
{
    public async Task<Result> Handle(ProductCreatedV2 integrationEvent, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "insert into inventory.product_categories (product_id, category_id, filed_by) values (@ProductId, @CategoryId, @FiledBy)",
                new { integrationEvent.ProductId, integrationEvent.CategoryId, FiledBy = context.Actor.Id },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return Result.Success();
    }
}
