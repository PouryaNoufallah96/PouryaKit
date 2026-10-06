using PouryaKit.Cqrs;
using PouryaKit.Security;

namespace Catalog.Products.Features.RemoveProducts;

/// <summary>Removes every product whose name, as stored, is one of these; returns how many it removed.</summary>
[RequiresPermission(RemoveProductsCommand.Permission)]
internal sealed record RemoveProductsCommand(IReadOnlyList<string> Names) : Command<int>
{
    public const string Permission = "catalog.products.remove";
}
