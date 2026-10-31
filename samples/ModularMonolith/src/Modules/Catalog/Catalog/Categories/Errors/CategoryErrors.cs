using PouryaKit.Domain;

namespace Catalog.Categories.Errors;

internal static class CategoryErrors
{
    public static readonly Error NameRequired = Error.Validation(
        "Category.NameRequired",
        "A category needs a name.",
        "Name"
    );

    public static readonly Error NotFound = Error.NotFound(
        "Category.NotFound",
        "The category does not exist."
    );
}
