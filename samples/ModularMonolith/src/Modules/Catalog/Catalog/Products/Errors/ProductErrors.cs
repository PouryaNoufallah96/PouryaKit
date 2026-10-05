using PouryaKit.Domain;

namespace Catalog.Products.Errors;

internal static class ProductErrors
{
    public static readonly Error NameRequired = Error.Validation("Product.NameRequired", "A product needs a name.", "Name");

    public static readonly Error NotFound = Error.NotFound("Product.NotFound", "The product does not exist.");
}
