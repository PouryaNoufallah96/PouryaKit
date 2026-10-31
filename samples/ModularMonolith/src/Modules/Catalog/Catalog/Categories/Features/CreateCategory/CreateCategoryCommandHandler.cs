using Catalog.Categories.Entities;
using Catalog.Categories.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace Catalog.Categories.Features.CreateCategory;

internal sealed class CreateCategoryCommandHandler(
    IRepository<Category, CategoryId> categories,
    OperationContext context
) : CommandHandler<CreateCategoryCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(
        CreateCategoryCommand command,
        CancellationToken cancellationToken
    )
    {
        // Catalog is tenant-scoped: an Operation without a tenant never reaches its handlers.
        var category = Category.Create(
            new CategoryId(command.CategoryId),
            command.Name,
            context.Tenant!
        );
        if (category.IsFailure)
        {
            return category.Errors;
        }

        await categories.AddAsync(category.Value, cancellationToken);
        return category.Value.Id.Value;
    }
}
