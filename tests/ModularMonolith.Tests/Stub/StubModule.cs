using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Web;

namespace ModularMonolith.Tests.Stub;

// A second Module, for tests only, with its own DbContext and schema beside Catalog's: with one Module in the Host, a
// Unit of Work resolved for the wrong Module would go unnoticed.
public sealed class StubModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.AddPouryaKitDapper(connectionName: "app", typeof(StubModule).Assembly);
        module.AddPouryaKitCqrs(typeof(StubModule).Assembly);
        module.AddPouryaKitNotificationHandlers(typeof(StubModule).Assembly, typeof(StubModule));
        module.Publish<NoteSharedV1>("stub.note-shared.v1", typeof(StubModule));
        module.Publish<NoteCopyRequestedV1>("stub.note-copy-requested.v1", typeof(StubModule));
        module.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("stub.note-shared", typeof(StubModule));
        module.Subscribe<NoteCopyRequestedV1, CopyRequestedNoteThenRefuseIntegrationEventHandler>(
            "stub.note-copy-requested",
            typeof(StubModule)
        );
        module.PublishSnapshot<Folder, FolderSnapshotV1, FolderSnapshotMapper>("stub.folder-snapshot.v1", typeof(StubModule));
        module.PublishSnapshot<Tray, TraySnapshotV1, TraySnapshotMapper>("stub.tray-snapshot.v1", typeof(StubModule));
        module.Subscribe<FolderSnapshotV1, RecordFolderSnapshotIntegrationEventHandler>("stub.folder-snapshots", typeof(StubModule));
        module.Subscribe<TraySnapshotV1, RecordTraySnapshotIntegrationEventHandler>("stub.tray-snapshots", typeof(StubModule));
        module.Publish<NoteCirculatedV1>("stub.note-circulated.v1", typeof(StubModule));
        module.Subscribe<NoteCirculatedV1, LedgerCirculatedNoteIntegrationEventHandler>(Circulation.Ledger, typeof(StubModule));
        module.Subscribe<NoteCirculatedV1, ArchiveCirculatedNoteIntegrationEventHandler>(Circulation.Archive, typeof(StubModule));
        module.AddSingleton<IntegrationEventLog>();
        module.AddScoped<ReactionLog>();
        module.AddSingleton<DeliveryLog>();
        module.AddSingleton<CatalogQuestionContexts>();
        module.AddPouryaKitEndpoints("/stub", typeof(StubModule).Assembly);
    }
}
