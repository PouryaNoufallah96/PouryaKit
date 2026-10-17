using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// Runs when the model is finished, so it also covers entities a derived DbContext maps after calling the base: every
// aggregate's Version is its concurrency token, every soft-deletable entity is left out of queries, and so is every
// tenant-scoped entity of another tenant than the Operation's, whose tenant is a concurrency token too: an UPDATE or
// DELETE matches the row only under the tenant the entity was loaded or attached with. A part's key to the entity that
// holds it in a tenant-scoped aggregate is a concurrency token as well, so its write matches the row only under the holder
// it was loaded with, the one the prepare step judged.
internal sealed class PouryaKitModelConvention(PouryaKitDbContext dbContext) : IModelFinalizingConvention
{
    private static readonly PropertyInfo OperationTenant = typeof(PouryaKitDbContext).GetProperty(
        nameof(PouryaKitDbContext.Tenant),
        BindingFlags.Instance | BindingFlags.NonPublic
    )!;

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context
    )
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (
                var holderKey in entityType
                    .GetDeclaredForeignKeys()
                    .Where(foreignKey => foreignKey.PrincipalToDependent is not null && InTenantScopedAggregate(foreignKey.PrincipalEntityType, []))
                    .SelectMany(foreignKey => foreignKey.Properties)
                    .Where(property => !property.IsPrimaryKey())
            )
            {
                holderKey.Builder.IsConcurrencyToken(true);
            }

            // Filters and tokens belong to the root of a hierarchy; an owned type is reached through its owner.
            if (entityType.BaseType is not null || entityType.IsOwned())
            {
                EnsureFiltered(entityType, typeof(ISoftDeletable), "leave its deleted rows out");
                EnsureFiltered(entityType, typeof(ITenantScoped), "leave out the rows of other tenants");
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

            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                entityType.Builder.HasQueryFilter(PouryaKitDbContext.TenantFilter, OfTheOperationsTenant(entityType.ClrType));
                entityType.FindProperty(nameof(ITenantScoped.Tenant))?.Builder.IsConcurrencyToken(true);
            }
        }
    }

    // The holder is tenant-scoped, or a part held, through the navigations the prepare step walks, by one that is.
    private static bool InTenantScopedAggregate(IConventionEntityType holder, HashSet<IConventionEntityType> seen) =>
        typeof(ITenantScoped).IsAssignableFrom(holder.ClrType)
        || (
            !typeof(IAggregateRoot).IsAssignableFrom(holder.ClrType)
            && seen.Add(holder)
            && holder
                .GetForeignKeys()
                .Any(foreignKey => foreignKey.PrincipalToDependent is not null && InTenantScopedAggregate(foreignKey.PrincipalEntityType, seen))
        );

    // Such a type would be read whatever the filter says: EF Core filters only the root of a hierarchy.
    private static void EnsureFiltered(IConventionEntityType entityType, Type contract, string filtered)
    {
        if (
            contract.IsAssignableFrom(entityType.ClrType)
            && (entityType.IsOwned() || !contract.IsAssignableFrom(entityType.GetRootType().ClrType))
        )
        {
            throw new InvalidOperationException(
                $"{entityType.ClrType.Name} is {contract.Name} but is mapped as {(entityType.IsOwned() ? "an owned type" : $"a type derived from {entityType.GetRootType().ClrType.Name}, which is not")}, so no query filter can {filtered}. Implement {contract.Name} on an aggregate or entity that is not owned, at the root of its hierarchy."
            );
        }
    }

    private static LambdaExpression NotDeleted(Type type)
    {
        var entity = Expression.Parameter(type, "entity");
        return Expression.Lambda(Expression.Not(Expression.Property(entity, nameof(ISoftDeletable.IsDeleted))), entity);
    }

    // The model is cached and shared by every instance of the DbContext's type. EF Core replaces this instance in the
    // filter with the DbContext that runs each query, so the filter reads that query's Operation, as a filter written in
    // OnModelCreating over a member of the DbContext does.
    private LambdaExpression OfTheOperationsTenant(Type type)
    {
        var entity = Expression.Parameter(type, "entity");
        return Expression.Lambda(
            Expression.Equal(
                Expression.Property(entity, nameof(ITenantScoped.Tenant)),
                Expression.Property(Expression.Constant(dbContext, typeof(PouryaKitDbContext)), OperationTenant)
            ),
            entity
        );
    }
}
