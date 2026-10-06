using Catalog.Products.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Security;
using PouryaKit.Validation;
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
        module.AddPouryaKitValidation(typeof(StubModule).Assembly);
        module.AddPouryaKitPermissions(typeof(StubModule).Assembly);
        module.AddSingleton<StampAuthorizations>();
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
        module.PublishSnapshot<Shelf, ShelfSnapshotV1, ShelfSnapshotMapper>("stub.shelf-snapshot.v1", typeof(StubModule));
        module.Subscribe<ShelfSnapshotV1, RecordSnapshotIntegrationEventHandler<ShelfSnapshotV1>>("stub.shelf-snapshots", typeof(StubModule));
        module.PublishSnapshot<Counter, CounterSnapshotV1, CounterSnapshotMapper>("stub.counter-snapshot.v1", typeof(StubModule));
        module.PublishSnapshot<AggregateRoot<ProductId>, ProductKeyedSnapshotV1, ProductKeyedSnapshotMapper>(
            "stub.product-keyed-snapshot.v1",
            typeof(StubModule)
        );
        module.Subscribe<ProductKeyedSnapshotV1, RecordSnapshotIntegrationEventHandler<ProductKeyedSnapshotV1>>(
            "stub.product-keyed-snapshots",
            typeof(StubModule)
        );
        module.PublishSnapshot<Case, CaseSnapshotV1, CaseSnapshotMapper>("stub.case-snapshot.v1", typeof(StubModule));
        module.Subscribe<CaseSnapshotV1, RecordSnapshotIntegrationEventHandler<CaseSnapshotV1>>("stub.case-snapshots", typeof(StubModule));
        module.PublishSnapshot<TimestampKey, TimestampKeySnapshotV1, TimestampKeySnapshotMapper>("stub.timestamp-key-snapshot.v1", typeof(StubModule));
        module.Subscribe<TimestampKeySnapshotV1, CopyTimestampKeyIntegrationEventHandler>("stub.timestamp-key-snapshots", typeof(StubModule));
        module.PublishSnapshot<BinaryKey, BinaryKeySnapshotV1, BinaryKeySnapshotMapper>("stub.binary-key-snapshot.v1", typeof(StubModule));
        module.Subscribe<BinaryKeySnapshotV1, CopyBinaryKeyIntegrationEventHandler>("stub.binary-key-snapshots", typeof(StubModule));
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
