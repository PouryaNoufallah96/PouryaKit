using PouryaKit.Caching;
using PouryaKit.Cqrs;

namespace Catalog.Products.Features.GetProduct;

#region get-product-cacheable-query
internal sealed record GetProductQuery(Guid ProductId) : Query<ProductDto>, ICacheableQuery
{
    /// <summary>The tag of every cached read of one product, which a command that changes the product declares.</summary>
    public static string ProductTag(Guid productId) => $"product:{productId}";

    /// <summary>The tag every cached product read carries, for a command that cannot name the products it changes.</summary>
    public const string ProductsTag = "products";

    public string CacheKey => ProductId.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    public IReadOnlyCollection<string> CacheTags => [ProductTag(ProductId), ProductsTag];
}
#endregion
