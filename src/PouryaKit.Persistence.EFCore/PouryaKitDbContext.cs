using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The base of a Module's DbContext. It applies the Kit's conventions once, so a mapping states only what is specific:
/// every typed ID in the DbContext's assembly is stored as its value, Domain Events are never mapped, every
/// <see cref="IEntityTypeConfiguration{TEntity}"/> in the assembly is applied, and the default schema is the schema of
/// the migrations history table. Every aggregate's <see cref="AggregateRoot{TId}.Version"/> is its concurrency token,
/// and every <see cref="ISoftDeletable"/> entity is left out of queries by a filter named
/// <c>PouryaKit.SoftDelete</c>. Override <see cref="OnModelCreating"/> only to add to it, and call the base.
/// </summary>
public abstract class PouryaKitDbContext : DbContext
{
    protected PouryaKitDbContext(DbContextOptions options)
        : base(options) =>
        // A removed soft-deletable aggregate is kept, so nothing it owns may be deleted with it before the Unit of Work
        // turns the removal into a mark; a removal that stays one still cascades when it is saved.
        ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new PouryaKitModelConvention());
        configurationBuilder.IgnoreAny<DomainEvent>();
        foreach (var type in GetType().Assembly.GetTypes())
        {
            if (TypedId.GetValueType(type) is { } valueType)
            {
                configurationBuilder
                    .Properties(type)
                    .HaveConversion(typeof(TypedIdConverter<,>).MakeGenericType(type, valueType));
            }
        }
    }

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
