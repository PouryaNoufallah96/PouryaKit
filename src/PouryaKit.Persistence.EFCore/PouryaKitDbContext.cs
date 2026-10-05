using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The base of a Module's DbContext. It applies the Kit's conventions once, so a mapping states only what is specific:
/// every typed ID in the DbContext's assembly is stored as its value, Domain Events are never mapped, every
/// <see cref="IEntityTypeConfiguration{TEntity}"/> in the assembly is applied, and the default schema is the schema of
/// the migrations history table. Override <see cref="OnModelCreating"/> only to add to it, and call the base.
/// </summary>
public abstract class PouryaKitDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
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
