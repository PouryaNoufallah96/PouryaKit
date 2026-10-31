using Catalog.Products.Features.GetProduct;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Security;

namespace Catalog.Products.Features.RemoveProducts;

#region remove-products-command
/// <summary>Removes every product whose name, as stored, is one of these; returns how many it removed.</summary>
[RequiresPermission(RemoveProductsCommand.Permission)]
internal sealed record RemoveProductsCommand(IReadOnlyList<string> Names)
    : Command<int>,
        IInvalidatesCache
{
    public const string Permission = "catalog.products.remove";

    // It finds the products by name, so it cannot name their ids before it runs: it evicts every cached product read.
    public IReadOnlyCollection<string> InvalidatedCacheTags => [GetProductQuery.ProductsTag];
}
#endregion
