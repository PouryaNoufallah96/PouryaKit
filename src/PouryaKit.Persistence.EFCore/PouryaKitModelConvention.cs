using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// Runs when the model is finished, so it also covers entities a derived DbContext maps after calling the base: every
// aggregate's Version is its concurrency token, and every soft-deletable entity is left out of queries.
internal sealed class PouryaKitModelConvention : IModelFinalizingConvention
{
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context
    )
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            // Filters and tokens belong to the root of a hierarchy; an owned type is reached through its owner.
            if (entityType.BaseType is not null || entityType.IsOwned())
            {
                // Such a type would be marked deleted on removal, yet still be read: EF Core filters only the root.
                if (
                    typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType)
                    && (entityType.IsOwned() || !typeof(ISoftDeletable).IsAssignableFrom(entityType.GetRootType().ClrType))
                )
                {
                    throw new InvalidOperationException(
                        $"{entityType.ClrType.Name} is ISoftDeletable but is mapped as {(entityType.IsOwned() ? "an owned type" : $"a type derived from {entityType.GetRootType().ClrType.Name}, which is not")}, so no query filter can leave its deleted rows out. Implement ISoftDeletable on an aggregate or entity that is not owned, at the root of its hierarchy."
                    );
                }

                continue;
            }

            if (typeof(IAggregateRoot).IsAssignableFrom(entityType.ClrType))
            {
                entityType.FindProperty(nameof(IAggregateRoot.Version))?.Builder.IsConcurrencyToken(true);
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                entityType.Builder.HasQueryFilter(PouryaKitDbContext.SoftDeleteFilter, NotDeleted(entityType.ClrType));
            }
        }
    }

    private static LambdaExpression NotDeleted(Type type)
    {
        var entity = Expression.Parameter(type, "entity");
        return Expression.Lambda(Expression.Not(Expression.Property(entity, nameof(ISoftDeletable.IsDeleted))), entity);
    }
}
