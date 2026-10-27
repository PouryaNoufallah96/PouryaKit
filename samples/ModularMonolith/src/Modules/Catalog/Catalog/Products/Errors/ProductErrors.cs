using PouryaKit.Domain;

namespace Catalog.Products.Errors;

#region product-errors
internal static class ProductErrors
{
    public static readonly Error NameRequired = Error.Validation("Product.NameRequired", "A product needs a name.", "Name");

    public static readonly Error NotFound = Error.NotFound("Product.NotFound", "The product does not exist.");

    // Mapped to the unique index on the name in CatalogModule: the database, not a check beforehand, refuses a duplicate.
    public static readonly Error NameInUse = Error.Conflict("Product.NameInUse", "Another product already has this name.");

    public static readonly Error NameRepeated = Error.Validation(
        "Product.NameRepeated",
        "The same product name is given more than once.",
        "Names"
    );

    // The Host's guardrail refused a question about the products, or the answer to it: the refusal is the message.
    public static Error QuestionRefused(string refusal) => Error.Validation("Product.QuestionRefused", refusal);
}
#endregion
