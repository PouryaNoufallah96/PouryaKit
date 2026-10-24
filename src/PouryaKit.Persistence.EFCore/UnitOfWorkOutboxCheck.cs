using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

// A Host with an Outbox in which the EF Core Unit of Work has none: its Operations would commit their changes while what
// they publish or schedule is never written, and nothing would say so. Only the messaging package's EF Core registration
// gives the EF Core Unit of Work its Outbox; another write model's Outbox marks the Host without writing in the DbContext's
// transaction.
internal sealed class UnitOfWorkOutboxCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var isService = services.GetRequiredService<IServiceProviderIsService>();
        if (!isService.IsService(typeof(IOutbox)) || isService.IsService(typeof(IUnitOfWorkOutbox)))
        {
            return [];
        }

        var modules = composition
            .Modules.Where(module => services.GetKeyedService<DbContextConnection>(module) is not null)
            .Select(module => module.Name)
            .DefaultIfEmpty("the application");
        return [
            $"The Operations of {string.Join(", ", modules)} would commit their changes and never write what they publish or schedule: the Host has an Outbox, but none in the EF Core Unit of Work. Call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore on the Host, before UsePouryaKitMarten.",
        ];
    }
}
