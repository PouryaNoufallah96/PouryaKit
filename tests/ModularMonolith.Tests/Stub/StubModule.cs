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
        module.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.AddPouryaKitDapper(connectionName: "app");
        module.AddPouryaKitCqrs();
        module.AddPouryaKitValidation();
        module.AddPouryaKitPermissions();
        module.AddSingleton<StampAuthorizations>();
        module.AddSingleton<ContinuationProbes>();
        module.AddPouryaKitNotificationHandlers();
        module.Publish<NoteSharedV1>("stub.note-shared.v1");
        module.Publish<NoteCopyRequestedV1>("stub.note-copy-requested.v1");
        module.Subscribe<NoteSharedV1, RecordSharedNoteIntegrationEventHandler>("stub.note-shared");
        module.Subscribe<NoteCopyRequestedV1, CopyRequestedNoteThenRefuseIntegrationEventHandler>(
            "stub.note-copy-requested"
        );
        module.PublishSnapshot<Folder, FolderSnapshotV1, FolderSnapshotMapper>("stub.folder-snapshot.v1");
        module.PublishSnapshot<Tray, TraySnapshotV1, TraySnapshotMapper>("stub.tray-snapshot.v1");
        module.Subscribe<FolderSnapshotV1, RecordFolderSnapshotIntegrationEventHandler>("stub.folder-snapshots");
        module.Subscribe<TraySnapshotV1, RecordTraySnapshotIntegrationEventHandler>("stub.tray-snapshots");
        module.PublishSnapshot<Shelf, ShelfSnapshotV1, ShelfSnapshotMapper>("stub.shelf-snapshot.v1");
        module.Subscribe<ShelfSnapshotV1, RecordSnapshotIntegrationEventHandler<ShelfSnapshotV1>>("stub.shelf-snapshots");
        module.PublishSnapshot<Counter, CounterSnapshotV1, CounterSnapshotMapper>("stub.counter-snapshot.v1");
        module.PublishSnapshot<AggregateRoot<ProductId>, ProductKeyedSnapshotV1, ProductKeyedSnapshotMapper>(
            "stub.product-keyed-snapshot.v1"
        );
        module.Subscribe<ProductKeyedSnapshotV1, RecordSnapshotIntegrationEventHandler<ProductKeyedSnapshotV1>>(
            "stub.product-keyed-snapshots"
        );
        module.PublishSnapshot<Case, CaseSnapshotV1, CaseSnapshotMapper>("stub.case-snapshot.v1");
        module.Subscribe<CaseSnapshotV1, RecordSnapshotIntegrationEventHandler<CaseSnapshotV1>>("stub.case-snapshots");
        module.PublishSnapshot<TimestampKey, TimestampKeySnapshotV1, TimestampKeySnapshotMapper>("stub.timestamp-key-snapshot.v1");
        module.Subscribe<TimestampKeySnapshotV1, CopyTimestampKeyIntegrationEventHandler>("stub.timestamp-key-snapshots");
        module.PublishSnapshot<BinaryKey, BinaryKeySnapshotV1, BinaryKeySnapshotMapper>("stub.binary-key-snapshot.v1");
        module.Subscribe<BinaryKeySnapshotV1, CopyBinaryKeyIntegrationEventHandler>("stub.binary-key-snapshots");
        module.PublishSnapshot<Ticket, TicketSnapshotV1, TicketSnapshotMapper>("stub.ticket-snapshot.v1");
        module.Subscribe<TicketSnapshotV1, RecordSnapshotIntegrationEventHandler<TicketSnapshotV1>>("stub.ticket-snapshots");
        module.PublishSnapshot<Drawer, DrawerSnapshotV1, DrawerSnapshotMapper>("stub.drawer-snapshot.v1");
        module.Subscribe<DrawerSnapshotV1, RecordSnapshotIntegrationEventHandler<DrawerSnapshotV1>>("stub.drawer-snapshots");
        module.Publish<NoteCirculatedV1>("stub.note-circulated.v1");
        module.Subscribe<NoteCirculatedV1, LedgerCirculatedNoteIntegrationEventHandler>(Circulation.Ledger);
        module.Subscribe<NoteCirculatedV1, ArchiveCirculatedNoteIntegrationEventHandler>(Circulation.Archive);
        module.AddPouryaKitDelayedCommand<WriteDelayedNoteCommand>("stub.delayed-note");
        module.AddSingleton<IntegrationEventLog>();
        module.AddScoped<ReactionLog>();
        module.AddSingleton<DeliveryLog>();
        module.AddSingleton<NoteRuns>();
        module.AddSingleton<CachedQueryRuns>();
        module.AddSingleton<CatalogQuestionContexts>();
        module.AddPouryaKitEndpoints("/stub");
    }
}
