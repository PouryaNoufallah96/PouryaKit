using Catalog.Contracts.Products.Events.V1;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Marten;

namespace ModularMonolith.Tests.Streams;

/// <summary>The Accounts Stream Module's Composition Root.</summary>
public sealed class AccountsModule : IModuleDefinition
{
    /// <summary>The Module's schema, where its Receipts, Snapshot versions and idempotency keys are kept.</summary>
    public const string Schema = "accounts";

    public void Configure(ModuleBuilder module)
    {
        module.AddPouryaKitStreams(Schema);
        module.AddPouryaKitCqrs();
        module.AddPouryaKitNotificationHandlers();
        module.Publish<AccountOpenedV1>("accounts.account-opened.v1");
        module.Subscribe<AccountOpenedV1, NoteAccountOpened>("accounts.account-opened");
        module.Subscribe<ProductSnapshotV1, NoteProductSnapshot>("accounts.catalog-products");
    }
}
