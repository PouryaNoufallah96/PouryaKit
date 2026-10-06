using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;

namespace ModularMonolith.Tests.Stub;

// Snapshots the Proving Sample must not hold: of a folder, whose owned label and child pages and stickers are part of
// it, with a mapper that changes its folder when asked to; and of a tray, which is deleted outright when removed. The
// Stub publishes them and records every one it receives, and a Notification Handler publishes one by hand, as nothing
// may.

internal sealed record FolderSnapshotV1(Guid FolderId, string Name, string Colour, int Pages, bool IsRemoved, long Version);

internal sealed class FolderSnapshotMapper : ISnapshotMapper<Folder, FolderSnapshotV1>
{
    /// <summary>The label colour that makes the mapper rename the folder it maps, as a mapper must not.</summary>
    public const string ChangesItsFolder = "renamed by its mapper";

    public FolderSnapshotV1 Map(Folder folder)
    {
        if (folder.Label.Colour == ChangesItsFolder)
        {
            folder.Rename(ChangesItsFolder);
        }

        return Of(folder, isRemoved: false);
    }

    public FolderSnapshotV1 Tombstone(Folder folder) => Of(folder, isRemoved: true);

    private static FolderSnapshotV1 Of(Folder folder, bool isRemoved) =>
        new(folder.Id.Value, folder.Name, folder.Label.Colour, folder.Pages.Count, isRemoved, folder.Version);
}

internal sealed record TraySnapshotV1(Guid TrayId, int Pins, bool IsRemoved, long Version);

internal sealed class TraySnapshotMapper : ISnapshotMapper<Tray, TraySnapshotV1>
{
    public TraySnapshotV1 Map(Tray tray) => new(tray.Id.Value, tray.Pins.Count, IsRemoved: false, tray.Version);

    public TraySnapshotV1 Tombstone(Tray tray) => new(tray.Id.Value, tray.Pins.Count, IsRemoved: true, tray.Version);
}

internal sealed class RecordFolderSnapshotIntegrationEventHandler(IntegrationEventLog log, OperationContext context, Operation operation)
    : IIntegrationEventHandler<FolderSnapshotV1>
{
    public Task<Result> Handle(FolderSnapshotV1 snapshot, CancellationToken cancellationToken)
    {
        log.Add(new Received(snapshot, context, operation));
        return Task.FromResult(Result.Success());
    }
}

internal sealed class RecordTraySnapshotIntegrationEventHandler(IntegrationEventLog log, OperationContext context, Operation operation)
    : IIntegrationEventHandler<TraySnapshotV1>
{
    public Task<Result> Handle(TraySnapshotV1 snapshot, CancellationToken cancellationToken)
    {
        log.Add(new Received(snapshot, context, operation));
        return Task.FromResult(Result.Success());
    }
}

internal sealed record FolderSnapshotPublishedByHandDomainEvent(Guid FolderId) : DomainEvent;

internal sealed class PublishFolderSnapshotByHandNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<FolderSnapshotPublishedByHandDomainEvent>
{
    public Task<Result> Handle(FolderSnapshotPublishedByHandDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        events.Publish(domainEvent.Id, new FolderSnapshotV1(domainEvent.FolderId, "by hand", "grey", 0, false, 99L));
        return Task.FromResult(Result.Success());
    }
}
