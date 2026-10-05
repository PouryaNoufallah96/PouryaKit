using PouryaKit.Cqrs;

namespace Catalog.Products.Features.ImportProducts;

/// <summary>Creates a product for each name the catalog does not have yet; returns how many it created.</summary>
internal sealed record ImportProductsCommand(IReadOnlyList<string> Names) : Command<int>;
