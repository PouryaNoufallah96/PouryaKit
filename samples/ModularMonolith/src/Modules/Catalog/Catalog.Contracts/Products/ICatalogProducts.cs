namespace Catalog.Contracts.Products;

/// <summary>
/// The questions about products that Catalog answers for other Modules, synchronously and inside the asking
/// Operation's chain of work. Inject it into a handler; Catalog runs each question as its own Operation.
/// </summary>
public interface ICatalogProducts
{
    /// <summary>The product with <paramref name="productId"/>, or null when Catalog has no such product.</summary>
    Task<ProductSummary?> FindProductAsync(Guid productId, CancellationToken cancellationToken = default);
}

public sealed record ProductSummary(Guid Id, string Name);
