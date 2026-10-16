using System.Runtime.CompilerServices;
using M2.Contracts;
using M2.Marten;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;

[assembly: InternalsVisibleTo("M2.Tests")]
[assembly: InternalsVisibleTo("M2.Host")]

namespace M2.Ledger;

/// <summary>The Ledger Stream Module's Composition Root.</summary>
public sealed class LedgerModule : IModuleDefinition
{
    /// <summary>Whether Ledger keeps its Receipts as Marten documents rather than rows of its Receipt table.</summary>
    public static bool DocumentReceipts { get; set; }

    /// <inheritdoc />
    public void Configure(ModuleBuilder module)
    {
        module.UseSpikeMartenStreams(schema: "ledger", DocumentReceipts);
        module.AddPouryaKitCqrs();
        module.AddPouryaKitNotificationHandlers();
        module.Publish<AccountOpenedV1>("ledger.account-opened.v1");
    }
}
