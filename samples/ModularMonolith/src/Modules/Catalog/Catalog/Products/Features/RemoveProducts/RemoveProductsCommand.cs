using PouryaKit.Cqrs;

namespace Catalog.Products.Features.RemoveProducts;

/// <summary>Removes every product whose name, as stored, is one of these; returns how many it removed.</summary>
internal sealed record RemoveProductsCommand(IReadOnlyList<string> Names) : Command<int>;
