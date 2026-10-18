using Ledger.Contracts.Accounts.Events.V1;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Marten;
using PouryaKit.Web;

namespace Ledger;

/// <summary>The Ledger Module's Composition Root: a Stream Module, whose accounts are the events they appended.</summary>
public sealed class LedgerModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        // First, so the tenant requirement runs before every other authorizer: the Host's Marten store keeps every
        // account under the tenant of the Operation that wrote it.
        module.AddPouryaKitTenancy();
        module.AddPouryaKitStreams(schema: "ledger");
        module.AddPouryaKitCqrs();
        module.AddPouryaKitNotificationHandlers();
        module.Publish<AccountOpenedV1>("ledger.account-opened.v1");
        module.AddPouryaKitEndpoints("/ledger");
    }
}
