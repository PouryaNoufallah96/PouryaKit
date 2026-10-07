using Ardalis.Specification;
using Microsoft.EntityFrameworkCore.Migrations;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

// The packed EF Core paging API, declared and mapped as a Feature's repository would, and the migration of a Module
// with an idempotent command; no database is needed to build it.
var page = new Page<NoteListItem>([], 1, NoteRepository.Paging.Declaration.MaxSize, 0);
Console.WriteLine(page.TotalPages + NoteRepository.Paging.Declaration.Sorts.Count + IdempotencyTable.Name.Length);

internal sealed class IdempotencyKeys : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIdempotencyTable("notes");

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIdempotencyTable("notes");
}

internal sealed record NoteId(Guid Value) : TypedId<Guid>(Value);

internal sealed class Note : AggregateRoot<NoteId>
{
    public string Text { get; private set; } = "";
}

internal sealed record NoteListItem(NoteId Id, string Text);

internal sealed class NoteRepository(IUnitOfWork unitOfWork) : EfRepository<Note, NoteId>(unitOfWork)
{
    public static readonly PageExpressions<Note> Paging = new PageExpressions<Note>(
        new PageDeclaration(["text"], [FilterField.Of<string>("text", FilterOperator.Contains)])
    )
        .OrderBy("text", note => note.Text)
        .ThenBy(note => note.Id)
        .Filter<string>("text", FilterOperator.Contains, text => note => note.Text.Contains(text));

    public Task<Page<NoteListItem>> PageAsync(PageRequest request, CancellationToken cancellationToken) =>
        PageAsync(new ListItems(), Paging, request, cancellationToken);

    private sealed class ListItems : Specification<Note, NoteListItem>
    {
        public ListItems() => Query.Select(note => new NoteListItem(note.Id, note.Text));
    }
}
