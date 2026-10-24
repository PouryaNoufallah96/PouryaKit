using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Core;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The base of a Module's DbContext. It applies the Kit's conventions once, so a mapping states only what is specific:
/// every typed ID in the DbContext's assembly is stored as its value, Domain Events are never mapped, every
/// <see cref="IEntityTypeConfiguration{TEntity}"/> in the assembly is applied, and the default schema is the schema of
/// the migrations history table. Every aggregate's <see cref="AggregateRoot{TId}.Version"/> is its concurrency token,
/// every <see cref="ISoftDeletable"/> entity is left out of queries by a filter named <see cref="SoftDeleteFilter"/>, and
/// every <see cref="ITenantScoped"/> entity of another tenant than the Operation's by a filter named
/// <see cref="TenantFilter"/>, its tenant a concurrency token too, so no write matches a row of another tenant than the
/// one the entity was loaded or attached with; a part's key to its holder in such an aggregate is one too, so no part's
/// write matches its row under another holder than the one it was loaded with, and none is read where its holder is not,
/// under the same filter. Override <see cref="OnModelCreating"/> only to add to it, and call the base.
/// </summary>
public abstract class PouryaKitDbContext : DbContext
{
    /// <summary>
    /// The name of the query filter that leaves soft-deleted rows out, <c>PouryaKit.SoftDelete</c>. A read that must see
    /// them passes it to EF Core's <c>IgnoreQueryFilters([PouryaKitDbContext.SoftDeleteFilter])</c>.
    /// </summary>
    public const string SoftDeleteFilter = "PouryaKit.SoftDelete";

    /// <summary>
    /// The name of the query filter that reads an <see cref="ITenantScoped"/> entity only when it belongs to the tenant
    /// of the Operation running the query, and a part of such an aggregate only when its holder is read,
    /// <c>PouryaKit.Tenant</c>; where the Operation has no tenant, or no Operation began, it reads none. A read that must
    /// cross tenants passes it to EF Core's <c>IgnoreQueryFilters([PouryaKitDbContext.TenantFilter])</c>.
    /// </summary>
    public const string TenantFilter = "PouryaKit.Tenant";

    // The scope's services, which hold its Operation, as EF Core's AddDbContext gives them to the options.
    private readonly IServiceProvider? _applicationServices;

    // Every entity a query of this DbContext materialized and tracked since. One detached, or cleared from the change
    // tracker, and then tracked again was attached, as is any other tracked entity that is not added.
    private readonly HashSet<object> _loaded = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Creates the DbContext with <paramref name="options"/> and the cascade timings the Unit of Work's soft delete
    /// relies on.
    /// </summary>
    protected PouryaKitDbContext(DbContextOptions options)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _applicationServices = options.FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
        // A removed soft-deletable aggregate is kept, so nothing it owns may be deleted with it before the Unit of Work
        // turns the removal into a mark; the Unit of Work cascades a removal that stays one when it prepares the save.
        ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;
        // An orphan is deleted as soon as changes are detected, so the Unit of Work sees it when it prepares the save,
        // and a soft-deletable one is kept; deleted later, inside the save, it would be neither.
        ChangeTracker.DeleteOrphansTiming = CascadeTiming.Immediate;
        ChangeTracker.Tracked += (_, tracked) =>
        {
            if (tracked.FromQuery)
            {
                _loaded.Add(tracked.Entry.Entity);
            }
            else
            {
                _loaded.Remove(tracked.Entry.Entity);
            }
        };
    }

    // Read by the tenant filter each time a query runs, never when the model is built or the DbContext created: the
    // migration tool and the composition checks build a DbContext where no Operation began.
    internal string? Tenant => _applicationServices?.GetService<Operation>()?.Context?.Tenant;

    // Read by the Unit of Work's prepare step, which saves a tenant's aggregate only when a query loaded it.
    internal bool WasLoaded(object entity) => _loaded.Contains(entity);

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Add(_ => new PouryaKitModelConvention(this));
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
