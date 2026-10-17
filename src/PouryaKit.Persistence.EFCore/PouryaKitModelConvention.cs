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
            var toHolders = ToHolders(entityType);
            foreach (var holderKey in toHolders.SelectMany(foreignKey => foreignKey.Properties).Where(property => !property.IsPrimaryKey()))
            {
                holderKey.Builder.IsConcurrencyToken(true);
            }

            // Filters and tokens belong to the root of a hierarchy; an owned type is reached through its owner.
            if (entityType.BaseType is not null || entityType.IsOwned())
            {
                EnsureFiltered(entityType, typeof(ISoftDeletable), "leave its deleted rows out");
                EnsureFiltered(entityType, typeof(ITenantScoped), "leave out the rows of other tenants");
                if (!entityType.IsOwned() && toHolders.Count > 0 && !typeof(ITenantScoped).IsAssignableFrom(entityType.GetRootType().ClrType))
                {
                    throw new InvalidOperationException(
                        $"{entityType.ClrType.Name} is held by {toHolders[0].PrincipalEntityType.ClrType.Name} in a tenant-scoped aggregate but is mapped as a type derived from {entityType.GetRootType().ClrType.Name}, which holds no key to it, so no query filter can leave out the rows of other tenants. Map the relationship to its holder on the root of the hierarchy."
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

            if (typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                entityType.Builder.HasQueryFilter(PouryaKitDbContext.TenantFilter, OfTheOperationsTenant(entityType.ClrType));
                entityType.FindProperty(nameof(ITenantScoped.Tenant))?.Builder.IsConcurrencyToken(true);
            }
            else if (toHolders.Count > 0)
            {
                RefuseHolderCycle(entityType, []);
                entityType.Builder.HasQueryFilter(PouryaKitDbContext.TenantFilter, WithAReadableHolder(entityType.ClrType, toHolders));
            }
        }
    }

    // The keys of a part to the entities that hold it in a tenant-scoped aggregate.
    private static List<IConventionForeignKey> ToHolders(IConventionEntityType entityType) =>
        [
            .. entityType
                .GetDeclaredForeignKeys()
                .Where(foreignKey => foreignKey.PrincipalToDependent is not null && InTenantScopedAggregate(foreignKey.PrincipalEntityType, [])),
        ];

    // A part's filter reads its holder's set with the holder's own filter, except a part's own set, which EF Core reads
    // unfiltered there. Two parts that each hold the other, directly or through others, would expand each other's filter
    // without end, a stack overflow at the first query of either, so the model is refused instead.
    private static void RefuseHolderCycle(IConventionEntityType part, List<IConventionEntityType> path)
    {
        if (path.IndexOf(part) is var start and >= 0)
        {
            throw new InvalidOperationException(
                $"{string.Join(" -> ", path.Skip(start).Append(part).Select(type => type.ClrType.Name))}: these parts of a tenant-scoped aggregate hold one another, so the query filter of each would read the next one's without end. Break the cycle: hold a part by the aggregate's root, or by a part that it does not hold in turn."
            );
        }

        path.Add(part);
        foreach (
            var holder in ToHolders(part)
                .Select(foreignKey => foreignKey.PrincipalEntityType.GetRootType())
                .Where(holder => holder != part && !typeof(ITenantScoped).IsAssignableFrom(holder.ClrType))
                .Distinct()
        )
        {
            RefuseHolderCycle(holder, path);
        }

        path.RemoveAt(path.Count - 1);
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
        var judged = toHolders
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
                var held = Expression.Call(
                    typeof(Queryable),
                    nameof(Queryable.Any),
                    [holderType],
                    new EntityQueryRootExpression((IEntityType)foreignKey.PrincipalEntityType),
                    Expression.Quote(Expression.Lambda(sameKey, holder))
                );
                // A part an optional key holds by nothing is not that holder's part, so that key does not judge it.
                return foreignKey.IsRequired ? (Expression)held : Expression.OrElse(NamesNoHolder(part, foreignKey), held);
            })
            .Aggregate(Expression.AndAlso);
        // A part none of whose keys names a holder belongs to no tenant, so no tenant reads it.
        return Expression.Lambda(
            toHolders.Any(foreignKey => foreignKey.IsRequired)
                ? judged
                : Expression.AndAlso(Expression.Not(toHolders.Select(foreignKey => NamesNoHolder(part, foreignKey)).Aggregate(Expression.AndAlso)), judged),
            part
        );
    }

    private static Expression NamesNoHolder(ParameterExpression part, IConventionForeignKey foreignKey) =>
        foreignKey
            .Properties.Where(property => property.IsNullable)
            .Select(property => Expression.Equal(PropertyOf(part, property, property.ClrType), Expression.Constant(null, property.ClrType)))
            .Aggregate<Expression>(Expression.OrElse);

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
