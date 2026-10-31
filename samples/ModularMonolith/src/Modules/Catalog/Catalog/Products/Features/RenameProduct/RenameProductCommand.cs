using Catalog.Products.Features.GetProduct;
using PouryaKit.Caching;
using PouryaKit.Cqrs;

namespace Catalog.Products.Features.RenameProduct;

#region rename-product-invalidates-cache
internal sealed record RenameProductCommand(Guid ProductId, string Name)
    : Command,
        IInvalidatesCache
{
    public IReadOnlyCollection<string> InvalidatedCacheTags =>
        [GetProductQuery.ProductTag(ProductId)];
}
#endregion
