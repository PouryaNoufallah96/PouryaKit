using PouryaKit.Domain;

namespace Catalog.Products.Errors;

internal static class ProductErrors
{
    public static readonly Error NameRequired = Error.Validation("Product.NameRequired", "A product needs a name.", "Name");

    public static readonly Error NotFound = Error.NotFound("Product.NotFound", "The product does not exist.");

    // Mapped to the unique index on the name in CatalogModule: the database, not a check beforehand, refuses a duplicate.
    public static readonly Error NameInUse = Error.Conflict("Product.NameInUse", "Another product already has this name.");
}
