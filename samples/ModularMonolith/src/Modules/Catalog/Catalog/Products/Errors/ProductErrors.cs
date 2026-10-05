using PouryaKit.Domain;

namespace Catalog.Products.Errors;

internal static class ProductErrors
{
    public static readonly Error NameRequired = Error.Validation("Product.NameRequired", "A product needs a name.", "Name");
}
