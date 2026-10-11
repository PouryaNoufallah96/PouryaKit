using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PouryaKit.Core;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The base of a Module's DbContext. It applies the Kit's conventions once, so a mapping states only what is specific:
/// every typed ID in the DbContext's assembly is stored as its value, Domain Events are never mapped, every
/// <see cref="IEntityTypeConfiguration{TEntity}"/> in the assembly is applied, and the default schema is the schema of
/// the migrations history table. Every aggregate's <see cref="AggregateRoot{TId}.Version"/> is its concurrency token,
/// and every <see cref="ISoftDeletable"/> entity is left out of queries by a filter named
/// <see cref="SoftDeleteFilter"/>. Override <see cref="OnModelCreating"/> only to add to it, and call the base.
/// </summary>
public abstract class PouryaKitDbContext : DbContext
{
    /// <summary>
    /// The name of the query filter that leaves soft-deleted rows out, <c>PouryaKit.SoftDelete</c>. A read that must see
    /// them passes it to EF Core's <c>IgnoreQueryFilters([PouryaKitDbContext.SoftDeleteFilter])</c>.
    /// </summary>
    public const string SoftDeleteFilter = "PouryaKit.SoftDelete";

    /// <summary>
    /// Creates the DbContext with <paramref name="options"/> and the cascade timings the Unit of Work's soft delete
    /// relies on.
    /// </summary>
    protected PouryaKitDbContext(DbContextOptions options)
        : base(options)
    {
        // A removed soft-deletable aggregate is kept, so nothing it owns may be deleted with it before the Unit of Work
        // turns the removal into a mark; the Unit of Work cascades a removal that stays one when it prepares the save.
        ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
        // An orphan is deleted as soon as changes are detected, so the Unit of Work sees it when it prepares the save,
        // and a soft-deletable one is kept; deleted later, inside the save, it would be neither.
        ChangeTracker.DeleteOrphansTiming = CascadeTiming.Immediate;
    }

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new PouryaKitModelConvention());
        configurationBuilder.IgnoreAny<DomainEvent>();
        foreach (var type in ImplementingTypes.Of(typeof(TypedId<>), GetType().Assembly))
        {
            if (TypedId.GetValueType(type) is { } valueType)
            {
                configurationBuilder
                    .Properties(type)
                    .HaveConversion(typeof(TypedIdConverter<,>).MakeGenericType(type, valueType));
            }
        }
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        // The persistence package puts the history table in the Module's schema, so the two cannot drift apart.
        if (RelationalOptionsExtension.Extract(this.GetService<IDbContextOptions>()).MigrationsHistoryTableSchema is { } schema)
        {
            modelBuilder.HasDefaultSchema(schema);
        }

        modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
    }
}
