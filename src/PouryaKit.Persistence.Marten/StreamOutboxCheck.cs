using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Marten;

// A Host with an Outbox in which the Stream Unit of Work has none: its Operations would commit their Streams' events while
// what they publish is never written, and nothing would say so. Only the messaging package's Marten registration gives the
// Stream Unit of Work its Outbox; another write model's Outbox marks the Host without writing in Marten's session.
internal sealed class StreamOutboxCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var isService = services.GetRequiredService<IServiceProviderIsService>();
        if (!isService.IsService(typeof(IOutbox)) || isService.IsService(typeof(IStreamOutbox)))
        {
            return [];
        }

        var modules = services.GetRequiredService<StreamModules>().Modules.Select(module => module?.Name ?? "the application");
        return [
            $"The Operations of {string.Join(", ", modules)} would commit their Streams' events and never write what they publish: the Host has an Outbox, but none in the Marten Unit of Work. Call UsePouryaKitMarten(connectionName) of PouryaKit.Messaging.Wolverine.Marten on the Host instead of AddPouryaKitMarten.",
        ];
    }
}
