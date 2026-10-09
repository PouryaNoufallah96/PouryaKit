using PouryaKit.Cqrs;

namespace Catalog.Products.Features.CreateProduct;

/// <summary>
/// Creates a product; returns its id. A client that repeats a request with the same request id, after a response it
/// never received, gets the first answer instead of a conflict on the id it chose.
/// </summary>
[Idempotent]
internal sealed record CreateProductCommand(Guid ProductId, Guid CategoryId, string Name) : Command<Guid>;
