using Catalog.Products.Data.Repository;
using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

// Sent through Catalog's Gateway, so it runs in Catalog's Unit of Work; StubModule only registers it, from this
// assembly. It adds a product through the generic repository and finds it by ID through the product repository
// before anything is saved: only a shared DbContext returns the same instance.
internal sealed record ProbeProductRepositoriesCommand(Guid ProductId) : Command<bool>;

internal sealed class ProbeProductRepositoriesCommandHandler(
    IRepository<Product, ProductId> generic,
    IProductRepository specific
) : CommandHandler<ProbeProductRepositoriesCommand, bool>
{
    public override async Task<Result<bool>> Handle(
        ProbeProductRepositoriesCommand command,
        CancellationToken cancellationToken
    )
    {
        var id = new ProductId(command.ProductId);
        var product = Product.Create(id, "Lamp").Value;
        await generic.AddAsync(product, cancellationToken);
        return ReferenceEquals(product, await specific.FindAsync(id, cancellationToken));
    }
}
