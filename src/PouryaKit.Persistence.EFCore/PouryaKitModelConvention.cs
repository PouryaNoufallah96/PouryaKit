using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// Runs when the model is finished, so it also covers entities a derived DbContext maps after calling the base: every
// aggregate's Version is its concurrency token, and every soft-deletable entity is left out of queries.
internal sealed class PouryaKitModelConvention : IModelFinalizingConvention
{
    /// <summary>The name of the query filter that leaves soft-deleted rows out, for <c>IgnoreQueryFilters</c>.</summary>
    public const string SoftDeleteFilter = "PouryaKit.SoftDelete";

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
                continue;
            }

            if (typeof(IAggregateRoot).IsAssignableFrom(entityType.ClrType))
            {
                entityType.FindProperty(nameof(IAggregateRoot.Version))?.Builder.IsConcurrencyToken(true);
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                entityType.Builder.HasQueryFilter(SoftDeleteFilter, NotDeleted(entityType.ClrType));
            }
        }
    }

    private static LambdaExpression NotDeleted(Type type)
    {
        var entity = Expression.Parameter(type, "entity");
        return Expression.Lambda(Expression.Not(Expression.Property(entity, nameof(ISoftDeletable.IsDeleted))), entity);
    }
}
