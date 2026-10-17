using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

namespace PouryaKit.Messaging.Wolverine.EFCore;

// A Snapshot a tenant-scoped Module, or a tenant-scoped application, declares of an aggregate whose key two tenants could
// share. A Snapshot's identity and its version's key hold the aggregate's key and no tenant (ADR 0006, #117 amendment), so
// a receiver would take one tenant's Snapshot for another's, and apply one of the two only. A key that includes the
// tenant, or a single Guid, which the application generates, is unique across tenants. The key is the one each DbContext's
// model maps, read from a DbContext created in the check's own scope, as MessageStoreDatabaseCheck creates them; one whose
// named connection string the configuration lacks is DbContextConnectionsCheck's line. Nothing connects.
internal sealed class SnapshotKeyTenancyCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var mappers = scope.ServiceProvider.GetRequiredService<ISnapshotMappers>();
        Type?[] scopes = [null, .. composition.Modules];
        var declared = scopes
            .Where(composition.IsTenantScoped)
            .SelectMany(module => mappers.DeclaredAggregates(module).Select(aggregate => (Module: module, Aggregate: aggregate)))
            .ToList();
        if (declared.Count == 0)
        {
            yield break;
        }

        var configuration = services.GetRequiredService<IConfiguration>();
        List<IEntityType> roots =
        [
            .. services
                .GetServices<DbContextConnection>()
                .Where(connection => connection.Name is not { } name || !string.IsNullOrWhiteSpace(configuration.GetConnectionString(name)))
                .SelectMany(connection => ((DbContext)scope.ServiceProvider.GetRequiredService(connection.DbContext)).Model.GetEntityTypes())
                .Where(type => !type.IsOwned() && type.BaseType is null),
        ];
        foreach (var (module, aggregate) in declared)
        {
            foreach (var root in roots.Where(root => root.GetDerivedTypesInclusive().Any(type => aggregate.IsAssignableFrom(type.ClrType))))
            {
                var key = root.FindPrimaryKey()!.Properties;
                if (!IsTenantUnique(root, key))
                {
                    yield return $"{module?.Name ?? "The application"} is tenant-scoped, but it publishes the Snapshot of {root.ClrType.FullName}, whose key ({string.Join(", ", key.Select(property => property.Name))}) neither includes the tenant nor is a Guid, so two tenants' aggregates could share a Snapshot's identity and version, and a receiver would apply only one of them. Key {root.ClrType.Name} by a Guid the application sets, such as a Guid version 7, or include its tenant in the key.";
                }
            }
        }
    }

    private static bool IsTenantUnique(IEntityType root, IReadOnlyList<IProperty> key) =>
        (typeof(ITenantScoped).IsAssignableFrom(root.ClrType) && key.Any(property => property.Name == nameof(ITenantScoped.Tenant)))
        || (key is [var only] && (only.GetTypeMapping().Converter?.ProviderClrType ?? only.ClrType) == typeof(Guid));
}
