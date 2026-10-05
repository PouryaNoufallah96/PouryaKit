using PouryaKit.Cqrs;

namespace Catalog.Products.Features.ImportProducts;

/// <summary>
/// Creates a product in the category for each name the catalog does not have yet; returns how many it created.
/// </summary>
internal sealed record ImportProductsCommand(Guid CategoryId, IReadOnlyList<string> Names) : Command<int>;
