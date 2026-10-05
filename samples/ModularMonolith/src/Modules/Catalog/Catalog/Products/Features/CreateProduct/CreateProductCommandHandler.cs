using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Products.Features.CreateProduct;

internal sealed class CreateProductCommandHandler(IRepository<Product, ProductId> products)
    : CommandHandler<CreateProductCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var product = Product.Create(new ProductId(command.ProductId), command.Name);
        if (product.IsFailure)
        {
            return product.Errors;
        }

        await products.AddAsync(product.Value, cancellationToken);
        return product.Value.Id.Value;
    }
}
