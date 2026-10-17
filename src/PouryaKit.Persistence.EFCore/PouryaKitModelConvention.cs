using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Query;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// Runs when the model is finished, so it also covers entities a derived DbContext maps after calling the base: every
// aggregate's Version is its concurrency token, every soft-deletable entity is left out of queries, and so is every
// tenant-scoped entity of another tenant than the Operation's, whose tenant is a concurrency token too: an UPDATE or
// DELETE matches the row only under the tenant the entity was loaded or attached with. A part's key to the entity that
// holds it in a tenant-scoped aggregate is a concurrency token as well, so its write matches the row only under the holder
// it was loaded with, the one the prepare step judged, and the part is read, under the same named filter, only where its
// holder is.
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
            var toHolders = entityType
                .GetDeclaredForeignKeys()
                .Where(foreignKey => foreignKey.PrincipalToDependent is not null && InTenantScopedAggregate(foreignKey.PrincipalEntityType, []))
                .ToList();
            foreach (var holderKey in toHolders.SelectMany(foreignKey => foreignKey.Properties).Where(property => !property.IsPrimaryKey()))
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
            else if (toHolders.Count > 0)
            {
                entityType.Builder.HasQueryFilter(PouryaKitDbContext.TenantFilter, WithAReadableHolder(entityType.ClrType, toHolders));
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

    // A part passes when each holder its keys name is in the holder's set, which carries the holder's own filters: the
    // root's tenant filter, or, for a part of a part, its holder's filter in turn. The set is the query root EF Core's own
    // convention makes of a DbContext's Set<T>() in a filter; that convention runs before this one, so this one writes the
    // root itself.
    private static LambdaExpression WithAReadableHolder(Type type, List<IConventionForeignKey> toHolders)
    {
        var part = Expression.Parameter(type, "part");
        return Expression.Lambda(
            toHolders
                .Select(foreignKey =>
                {
                    var holderType = foreignKey.PrincipalEntityType.ClrType;
                    var holder = Expression.Parameter(holderType, "holder");
                    var sameKey = foreignKey
                        .PrincipalKey.Properties.Zip(
                            foreignKey.Properties,
                            (key, reference) => Expression.Equal(PropertyOf(holder, key, reference.ClrType), PropertyOf(part, reference, reference.ClrType))
                        )
                        .Aggregate(Expression.AndAlso);
                    return (Expression)Expression.Call(
                        typeof(Queryable),
                        nameof(Queryable.Any),
                        [holderType],
                        new EntityQueryRootExpression((IEntityType)foreignKey.PrincipalEntityType),
                        Expression.Quote(Expression.Lambda(sameKey, holder))
                    );
                })
                .Aggregate(Expression.AndAlso),
            part
        );
    }

    // Both keys are read as the part's type for them: an optional one is nullable where the holder's is not.
    private static MethodCallExpression PropertyOf(ParameterExpression entity, IConventionProperty property, Type type) =>
        Expression.Call(typeof(EF), nameof(EF.Property), [type], entity, Expression.Constant(property.Name));

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
