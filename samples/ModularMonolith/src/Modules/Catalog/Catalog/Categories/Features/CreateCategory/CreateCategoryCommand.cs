using PouryaKit.Cqrs;

namespace Catalog.Categories.Features.CreateCategory;

internal sealed record CreateCategoryCommand(Guid CategoryId, string Name) : Command<Guid>;
