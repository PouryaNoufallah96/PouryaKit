using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record CreateFolderCommand(Guid FolderId) : Command;

internal sealed class CreateFolderCommandHandler(IRepository<Folder, FolderId> folders) : CommandHandler<CreateFolderCommand>
{
    public override async Task<Result> Handle(CreateFolderCommand command, CancellationToken cancellationToken)
    {
        await folders.AddAsync(
            Folder.Create(new FolderId(command.FolderId), "drafts", "grey", "first page", "second page"),
            cancellationToken
        );
        return Result.Success();
    }
}

internal enum FolderEdit
{
    Relabel,
    RewritePage,
    TearOutPage,
    Nothing,
    Remove,
    PeelSticker,
    RelabelThenRefuse,
}

internal sealed record EditFolderCommand(Guid FolderId, FolderEdit Edit, string Colour = "red") : Command;

internal sealed class EditFolderCommandHandler(IRepository<Folder, FolderId> folders) : CommandHandler<EditFolderCommand>
{
    public static readonly Error Refused = Error.Conflict("Folder.Refused", "The folder was refused after it was relabelled.");

    public override async Task<Result> Handle(EditFolderCommand command, CancellationToken cancellationToken)
    {
        var folder = (await folders.FindAsync(new FolderId(command.FolderId), cancellationToken))!;
        switch (command.Edit)
        {
            case FolderEdit.Relabel:
                folder.Relabel(command.Colour);
                break;
            case FolderEdit.RelabelThenRefuse:
                folder.Relabel(command.Colour);
                return Refused;
            case FolderEdit.RewritePage:
                folder.RewritePage("first page", "rewritten");
                break;
            case FolderEdit.TearOutPage:
                folder.TearOutPage("second page");
                break;
            case FolderEdit.Remove:
                folders.Remove(folder);
                break;
            case FolderEdit.PeelSticker:
                folder.PeelSticker();
                break;
            case FolderEdit.Nothing:
                break;
        }

        return Result.Success();
    }
}

// One Operation that changes a folder three ways: its own handler renames it, a nested command relabels it, and a
// Domain Event Handler of the rename adds a page.
internal sealed record ReworkFolderCommand(Guid FolderId) : Command;

internal sealed class ReworkFolderCommandHandler(IRepository<Folder, FolderId> folders, IDispatcher dispatcher)
    : CommandHandler<ReworkFolderCommand>
{
    public override async Task<Result> Handle(ReworkFolderCommand command, CancellationToken cancellationToken)
    {
        var folder = (await folders.FindAsync(new FolderId(command.FolderId), cancellationToken))!;
        folder.Rename("reworked");
        return await dispatcher.SendAsync(new EditFolderCommand(command.FolderId, FolderEdit.Relabel), cancellationToken);
    }
}

internal sealed class AddPageOnRenameReaction(IRepository<Folder, FolderId> folders)
    : IDomainEventHandler<FolderRenamedDomainEvent>
{
    public async Task<Result> Handle(FolderRenamedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var folder = (await folders.FindAsync(domainEvent.FolderId, cancellationToken))!;
        folder.AddPage("added on rename");
        return Result.Success();
    }
}

internal sealed record MovePageCommand(Guid From, Guid To, string Text) : Command;

internal sealed class MovePageCommandHandler(IRepository<Folder, FolderId> folders) : CommandHandler<MovePageCommand>
{
    public override async Task<Result> Handle(MovePageCommand command, CancellationToken cancellationToken)
    {
        var from = (await folders.FindAsync(new FolderId(command.From), cancellationToken))!;
        var to = (await folders.FindAsync(new FolderId(command.To), cancellationToken))!;
        from.MovePage(command.Text, to);
        return Result.Success();
    }
}

internal sealed record RewritePageUnloadedCommand(string Text) : Command;

// Feature code never touches its DbContext; this handler does, to change a page without loading its folder.
internal sealed class RewritePageUnloadedCommandHandler(StubDbContext dbContext) : CommandHandler<RewritePageUnloadedCommand>
{
    public override async Task<Result> Handle(RewritePageUnloadedCommand command, CancellationToken cancellationToken)
    {
        var page = await dbContext.Set<Page>().SingleAsync(page => page.Text == command.Text, cancellationToken);
        page.Rewrite("rewritten unloaded");
        return Result.Success();
    }
}

internal sealed record RemoveNotesCommand(string Text) : Command;

internal sealed class RemoveNotesCommandHandler(IRepository<Note, NoteId> notes) : CommandHandler<RemoveNotesCommand>
{
    public override async Task<Result> Handle(RemoveNotesCommand command, CancellationToken cancellationToken)
    {
        foreach (var note in await notes.ListAsync(new NotesWithText(command.Text), cancellationToken))
        {
            notes.Remove(note);
        }

        return Result.Success();
    }
}
