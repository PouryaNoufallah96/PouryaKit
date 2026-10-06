using FluentValidation;
using Ardalis.Specification;
using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Stub;

public enum ReadingPoint
{
    Query,
    Authorizer,
    Validator,
    OnlyReading,
}

public enum ReadingChange
{
    None,
    Root,
    Child,
    AddChild,
    OwnedValue,
    DomainEvent,
}

internal sealed record ReadFolderCommand(
    Guid FolderId,
    ReadingPoint Point,
    ReadingChange Change,
    bool Held = true,
    bool Swallow = false,
    bool QueryThrows = false
) : Command;

internal sealed class ReadFolderCommandHandler(
    IRepository<Folder, FolderId> folders,
    IRepository<Note, NoteId> notes,
    IDispatcher dispatcher,
    Operation operation
) : CommandHandler<ReadFolderCommand>
{
    public override async Task<Result> Handle(ReadFolderCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write("legitimate pending write"), cancellationToken);
        var folder = command.Held ? (await folders.FindAsync(new FolderId(command.FolderId), cancellationToken))! : null;
        folder?.Relabel("blue");
        try
        {
            if (command.Point == ReadingPoint.Query)
            {
                _ = await dispatcher.QueryAsync(new ReadFolderQuery(command.FolderId, folder, command.Change, command.QueryThrows), cancellationToken);
            }
            else if (command.Point == ReadingPoint.OnlyReading)
            {
                using var reading = operation.OnlyReading("A test step");
                ChangeFolderRead.Apply(folder!, command.Change);
            }
            else
            {
                _ = await dispatcher.SendAsync(new ReadFolderCheckCommand(folder!, command.Point, command.Change), cancellationToken);
            }
        }
        catch (InvalidOperationException) when (command.Swallow) { }

        return Result.Success();
    }
}

internal sealed record ReadFolderQuery(Guid FolderId, Folder? Held, ReadingChange Change, bool Throws) : Query<bool>;

internal sealed class ReadFolderQueryHandler(IRepository<Folder, FolderId> folders) : QueryHandler<ReadFolderQuery, bool>
{
    public static readonly Error Missing = Error.NotFound("Reading.Missing", "The query found nothing.");

    public override async Task<Result<bool>> Handle(ReadFolderQuery query, CancellationToken cancellationToken)
    {
        var folder = query.Held ?? (await folders.FindAsync(new FolderId(query.FolderId), cancellationToken))!;
        ChangeFolderRead.Apply(folder, query.Change);
        if (query.Throws)
        {
            throw new InvalidOperationException("The query threw after its read.");
        }

        return Missing;
    }
}

internal static class ChangeFolderRead
{
    public static void Apply(Folder folder, ReadingChange change)
    {
        switch (change)
        {
            case ReadingChange.Root:
                folder.Rename("changed in a read");
                break;
            case ReadingChange.Child:
                folder.RewritePage("first page", "changed in a read");
                break;
            case ReadingChange.AddChild:
                folder.AddPage("added in a read");
                break;
            case ReadingChange.OwnedValue:
                folder.Relabel("changed in a read");
                break;
            case ReadingChange.DomainEvent:
                folder.Rename(folder.Name);
                break;
        }
    }
}

internal sealed record ReadFolderCheckCommand(Folder Folder, ReadingPoint Point, ReadingChange Change) : Command;

internal sealed class ReadFolderCheckCommandAuthorizer : IAuthorizer<ReadFolderCheckCommand>
{
    public Task<Result> AuthorizeAsync(ReadFolderCheckCommand command, CancellationToken cancellationToken)
    {
        if (command.Point == ReadingPoint.Authorizer)
        {
            ChangeFolderRead.Apply(command.Folder, command.Change);
        }

        return Task.FromResult(Result.Success());
    }
}

internal sealed class ReadFolderCheckCommandValidator : AbstractValidator<ReadFolderCheckCommand>
{
    public ReadFolderCheckCommandValidator() => RuleFor(command => command).Custom((command, _) =>
    {
        if (command.Point == ReadingPoint.Validator)
        {
            ChangeFolderRead.Apply(command.Folder, command.Change);
        }
    });
}

internal sealed class ReadFolderCheckCommandHandler : CommandHandler<ReadFolderCheckCommand>
{
    public override Task<Result> Handle(ReadFolderCheckCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed record ReadFolderOuterCheckCommand(Guid FolderId, ReadingPoint Point) : Command;

internal sealed class ReadFolderOuterCheckCommandAuthorizer(IRepository<Folder, FolderId> folders) : IAuthorizer<ReadFolderOuterCheckCommand>
{
    public async Task<Result> AuthorizeAsync(ReadFolderOuterCheckCommand command, CancellationToken cancellationToken)
    {
        if (command.Point == ReadingPoint.Authorizer)
        {
            (await folders.FindAsync(new FolderId(command.FolderId), cancellationToken))!.Relabel("changed by outer authorization");
        }

        return Result.Success();
    }
}

internal sealed class ReadFolderOuterCheckCommandValidator : AbstractValidator<ReadFolderOuterCheckCommand>
{
    public ReadFolderOuterCheckCommandValidator(IRepository<Folder, FolderId> folders) => RuleFor(command => command).CustomAsync(async (command, _, cancellationToken) =>
    {
        if (command.Point == ReadingPoint.Validator)
        {
            (await folders.FindAsync(new FolderId(command.FolderId), cancellationToken))!.Relabel("changed by outer validation");
        }
    });
}

internal sealed class ReadFolderOuterCheckCommandHandler : CommandHandler<ReadFolderOuterCheckCommand>
{
    public override Task<Result> Handle(ReadFolderOuterCheckCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}

internal sealed record ReadShelfLabelsCommand(string Aisle, string Bay) : Command;

internal sealed class ReadShelfLabelsCommandHandler(IRepository<Shelf, string> shelves, IDispatcher dispatcher)
    : CommandHandler<ReadShelfLabelsCommand>
{
    public override async Task<Result> Handle(ReadShelfLabelsCommand command, CancellationToken cancellationToken)
    {
        var shelf = (await shelves.FindAsync(new ShelfReadLocation(command.Aisle, command.Bay), cancellationToken))!;
        shelf.AddLabel("legitimate pending label");
        _ = await dispatcher.QueryAsync(new ReadShelfLabelsQuery(shelf), cancellationToken);
        return Result.Success();
    }
}

internal sealed class ShelfReadLocation : Specification<Shelf>
{
    public ShelfReadLocation(string aisle, string bay) => Query.Where(shelf => shelf.Aisle == aisle && shelf.Bay == bay);
}

internal sealed record ReadShelfLabelsQuery(Shelf Shelf) : Query<bool>;

internal sealed class ReadShelfLabelsQueryHandler : QueryHandler<ReadShelfLabelsQuery, bool>
{
    public override Task<Result<bool>> Handle(ReadShelfLabelsQuery query, CancellationToken cancellationToken)
    {
        query.Shelf.AddLabel("changed in a read");
        return Task.FromResult<Result<bool>>(ReadFolderQueryHandler.Missing);
    }
}

internal sealed record ReadNoteEventsCommand(string Text, bool Reverse) : Command;

internal sealed class ReadNoteEventsCommandHandler(IRepository<Note, NoteId> notes, IDispatcher dispatcher)
    : CommandHandler<ReadNoteEventsCommand>
{
    public override async Task<Result> Handle(ReadNoteEventsCommand command, CancellationToken cancellationToken)
    {
        var note = (await notes.FindAsync(new NotesWithText(command.Text), cancellationToken))!;
        note.Raising(new ReadingNoteDomainEvent()).Raising(new ReadingNoteDomainEvent());
        _ = await dispatcher.QueryAsync(new ReadNoteEventsQuery(note, command.Reverse), cancellationToken);
        return Result.Success();
    }
}

internal sealed record ReadingNoteDomainEvent : DomainEvent;

internal sealed record ReadNoteEventsQuery(Note Note, bool Reverse) : Query<bool>;

internal sealed class ReadNoteEventsQueryHandler : QueryHandler<ReadNoteEventsQuery, bool>
{
    public override Task<Result<bool>> Handle(ReadNoteEventsQuery query, CancellationToken cancellationToken)
    {
        var events = query.Note.DomainEvents.ToArray();
        ((IAggregateRoot)query.Note).ClearDomainEvents();
        if (query.Reverse)
        {
            query.Note.Raising(events[1]).Raising(events[0]);
        }
        else
        {
            query.Note.Raising(new ReadingNoteDomainEvent()).Raising(new ReadingNoteDomainEvent());
        }

        return Task.FromResult<Result<bool>>(ReadFolderQueryHandler.Missing);
    }
}

internal sealed record ConstantReadQuery : Query<string>;

internal sealed class ConstantReadQueryHandler : QueryHandler<ConstantReadQuery, string>
{
    public override Task<Result<string>> Handle(ConstantReadQuery query, CancellationToken cancellationToken) => Task.FromResult<Result<string>>("read without persistence");
}

public enum ComplexReadingChange
{
    Scalar,
    NestedReference,
    NestedCollection,
    Add,
    Remove,
    Reverse,
    Replace,
    Nothing,
}

internal sealed class ReadCollectionRoot : AggregateRoot<Guid>
{
    public List<ReadCollectionPart> Parts { get; private set; } = [];
}

internal sealed class ReadCollectionPart
{
    public string Text { get; set; } = "";
    public ReadCollectionDetail Detail { get; set; } = new();
    public List<ReadCollectionLeaf> Children { get; set; } = [];
}

internal sealed class ReadCollectionDetail
{
    public int Number { get; set; }
}

internal sealed class ReadCollectionLeaf
{
    public string Text { get; set; } = "";
}

internal sealed class ReadCollectionDbContext(DbContextOptions<ReadCollectionDbContext> options) : PouryaKitDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        var root = builder.Entity<ReadCollectionRoot>();
        root.ToTable("read_collections", "stub");
        root.Property(aggregate => aggregate.Id).HasColumnName("id");
        root.Property(aggregate => aggregate.Version).HasColumnName("version");
        root.ComplexCollection(aggregate => aggregate.Parts, parts =>
        {
            parts.ToJson("parts");
            parts.ComplexProperty(part => part.Detail);
            parts.ComplexCollection(part => part.Children);
        });
    }
}

internal sealed record ReadCollectionCommand(Guid Id, ComplexReadingChange Change, bool Held) : Command;

internal sealed class ReadCollectionCommandHandler(IRepository<ReadCollectionRoot, Guid> roots, IDispatcher dispatcher)
    : CommandHandler<ReadCollectionCommand>
{
    public override async Task<Result> Handle(ReadCollectionCommand command, CancellationToken cancellationToken)
    {
        var held = command.Held ? await roots.FindAsync(command.Id, cancellationToken) : null;
        if (held is not null)
        {
            held.Parts[1].Text = "legitimate pending change";
        }

        try
        {
            _ = await dispatcher.QueryAsync(new ReadCollectionQuery(command.Id, held, command.Change), cancellationToken);
        }
        catch (InvalidOperationException) { }

        return Result.Success();
    }
}

internal sealed record ReadCollectionQuery(Guid Id, ReadCollectionRoot? Held, ComplexReadingChange Change) : Query<bool>;

internal sealed class ReadCollectionQueryHandler(IRepository<ReadCollectionRoot, Guid> roots) : QueryHandler<ReadCollectionQuery, bool>
{
    public override async Task<Result<bool>> Handle(ReadCollectionQuery query, CancellationToken cancellationToken)
    {
        var root = query.Held ?? (await roots.FindAsync(query.Id, cancellationToken))!;
        switch (query.Change)
        {
            case ComplexReadingChange.Scalar:
                root.Parts[0].Text = "changed";
                break;
            case ComplexReadingChange.NestedReference:
                root.Parts[0].Detail.Number++;
                break;
            case ComplexReadingChange.NestedCollection:
                root.Parts[0].Children[0].Text = "changed";
                break;
            case ComplexReadingChange.Add:
                root.Parts.Add(new ReadCollectionPart { Text = "added" });
                break;
            case ComplexReadingChange.Remove:
                root.Parts.RemoveAt(0);
                break;
            case ComplexReadingChange.Reverse:
                root.Parts.Reverse();
                break;
            case ComplexReadingChange.Replace:
                root.Parts[0] = new ReadCollectionPart { Text = "replacement" };
                break;
        }

        return ReadFolderQueryHandler.Missing;
    }
}
