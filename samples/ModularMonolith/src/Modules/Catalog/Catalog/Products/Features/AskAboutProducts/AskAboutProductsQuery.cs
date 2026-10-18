using PouryaKit.Cqrs;

namespace Catalog.Products.Features.AskAboutProducts;

/// <summary>A question about the catalog's products, answered by a model as the asking Actor; returns its answer.</summary>
internal sealed record AskAboutProductsQuery(string Question) : Query<string>;
