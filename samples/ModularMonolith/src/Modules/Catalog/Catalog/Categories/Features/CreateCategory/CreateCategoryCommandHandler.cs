using Catalog.Categories.Entities;
using Catalog.Categories.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Categories.Features.CreateCategory;

internal sealed class CreateCategoryCommandHandler(IRepository<Category, CategoryId> categories)
    : CommandHandler<CreateCategoryCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var category = Category.Create(new CategoryId(command.CategoryId), command.Name);
        if (category.IsFailure)
        {
            return category.Errors;
        }

        await categories.AddAsync(category.Value, cancellationToken);
        return category.Value.Id.Value;
    }
}
