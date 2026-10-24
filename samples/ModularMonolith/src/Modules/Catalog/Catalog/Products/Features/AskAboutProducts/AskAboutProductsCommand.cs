using PouryaKit.Cqrs;

namespace Catalog.Products.Features.AskAboutProducts;

/// <summary>
/// A question about the catalog's products, answered by a model as the asking Actor, in that Actor's conversation in the
/// Operation's tenant; returns its answer.
/// </summary>
internal sealed record AskAboutProductsCommand(string Question) : Command<string>;
