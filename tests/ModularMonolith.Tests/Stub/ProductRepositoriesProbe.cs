using Catalog.Categories.ValueObjects;
using Catalog.Products.Data.Repository;
using Catalog.Products.Data.Specifications;
using Catalog.Products.Entities;
using Catalog.Products.Features.RemoveProducts;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

// Run in a scope the test begins as a Catalog Operation, so it runs in Catalog's Unit of Work; StubModule only
// registers it, from this assembly, so Catalog's Gateway would refuse it. It adds a product through the generic repository and finds it by ID through the product repository
// before anything is saved: only a shared DbContext returns the same instance.
internal sealed record ProbeProductRepositoriesCommand(Guid ProductId, Guid CategoryId) : Command<bool>;

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
        var product = Product.Create(id, new CategoryId(command.CategoryId), "Lamp").Value;
        await generic.AddAsync(product, cancellationToken);
        return ReferenceEquals(product, await specific.FindAsync(id, cancellationToken));
    }
}

// Removes the same products twice in one Operation, as two nested commands that both select them would.
internal sealed record RemoveProductsTwiceCommand(string Name) : Command;

internal sealed class RemoveProductsTwiceCommandHandler(IDispatcher dispatcher) : CommandHandler<RemoveProductsTwiceCommand>
{
    public override async Task<Result> Handle(RemoveProductsTwiceCommand command, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync(new RemoveProductsCommand([command.Name]), cancellationToken);
        return await dispatcher.SendAsync(new RemoveProductsCommand([command.Name]), cancellationToken);
    }
}

// Asks the generic repository for a product by ID and by a specification, as a Catalog Feature would.
internal sealed record ProductsFound(bool ById, int ByName);

internal sealed record FindProductQuery(Guid ProductId, string Name) : Query<ProductsFound>;

internal sealed class FindProductQueryHandler(IRepository<Product, ProductId> products)
    : QueryHandler<FindProductQuery, ProductsFound>
{
    public override async Task<Result<ProductsFound>> Handle(FindProductQuery query, CancellationToken cancellationToken) =>
        new ProductsFound(
            await products.FindAsync(new ProductId(query.ProductId), cancellationToken) is not null,
            (await products.ListAsync(new ProductsNamed([query.Name]), cancellationToken)).Count
        );
}
