namespace Catalog.Contracts.Products.Realtime;

/// <summary>
/// A product was added to the catalog: the realtime event Catalog pushes on <see cref="Channel"/> to the browsers of the
/// product's tenant once the product is committed. It carries what a list shows at once; a client reads the rest
/// through a query.
/// </summary>
public sealed record NewProduct(Guid ProductId, string Name)
{
    /// <summary>The Channel Catalog pushes it on, which Catalog's SSE endpoint admits for every caller.</summary>
    public const string Channel = "catalog.new-products";
}
