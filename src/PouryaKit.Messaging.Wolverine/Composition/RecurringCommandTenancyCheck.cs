using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

// A recurring command declared in a tenant-scoped Module, or in a tenant-scoped application: each occurrence runs for its
// system Actor with no tenant, where every row belongs to one, and the Kit runs no recurring command per tenant.
internal sealed class RecurringCommandTenancyCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services) =>
        (services.GetService<DelayedCommandDeclarations>()?.All ?? [])
            .Where(declared =>
                declared.Recurring is not null && composition.IsTenantScoped(declared.Module)
            )
            .Select(declared =>
                $"The recurring command {declared.Name}, {declared.Command.FullName}, is declared in {declared.Module?.Name ?? "the application"}, which is tenant-scoped, but its occurrences run for the system Actor {declared.Recurring!.SystemActor} with no tenant, and the Kit runs no recurring command per tenant. Declare it in a Module that is not tenant-scoped."
            );
}
