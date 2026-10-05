using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record CaseId(Guid Value) : TypedId<Guid>(Value);

internal sealed record PencilId(Guid Value) : TypedId<Guid>(Value);

internal sealed record BookId(Guid Value) : TypedId<Guid>(Value);

// Two aggregates derived from one base, in one table, each with children of its own: the children's keys name
// different derived types and share the base's key.
internal abstract class Case : AggregateRoot<CaseId>;

internal sealed class PencilCase : Case
{
    private readonly List<Pencil> _pencils = [];

    private PencilCase() { }

    public PencilCase(CaseId id)
    {
        Id = id;
        _pencils.Add(new Pencil(new PencilId(Guid.CreateVersion7()), "grey"));
    }

    public IReadOnlyList<Pencil> Pencils => _pencils;

    public void Sharpen() => _pencils[0].Sharpen();
}

internal sealed class BookCase : Case
{
    private readonly List<Book> _books = [];

    private BookCase() { }

    public BookCase(CaseId id)
    {
        Id = id;
        _books.Add(new Book(new BookId(Guid.CreateVersion7()), "draft"));
    }

    public IReadOnlyList<Book> Books => _books;

    public void Retitle() => _books[0].Retitle();
}

internal sealed class Pencil : Entity<PencilId>
{
    private Pencil() { }

    public Pencil(PencilId id, string colour)
    {
        Id = id;
        Colour = colour;
    }

    public string Colour { get; private set; } = "";

    public void Sharpen() => Colour = "sharp";
}

internal sealed class Book : Entity<BookId>
{
    private Book() { }

    public Book(BookId id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Title { get; private set; } = "";

    public void Retitle() => Title = "final";
}

internal sealed class CaseConfiguration : IEntityTypeConfiguration<Case>
{
    public void Configure(EntityTypeBuilder<Case> builder) => builder.ToTable("cases").UseTphMappingStrategy();
}

internal sealed class PencilCaseConfiguration : IEntityTypeConfiguration<PencilCase>
{
    public void Configure(EntityTypeBuilder<PencilCase> builder)
    {
        builder.HasMany(pencilCase => pencilCase.Pencils).WithOne().IsRequired();
        builder.Navigation(pencilCase => pencilCase.Pencils).AutoInclude();
    }
}

internal sealed class BookCaseConfiguration : IEntityTypeConfiguration<BookCase>
{
    public void Configure(EntityTypeBuilder<BookCase> builder)
    {
        builder.HasMany(bookCase => bookCase.Books).WithOne().IsRequired();
        builder.Navigation(bookCase => bookCase.Books).AutoInclude();
    }
}

internal sealed class PencilConfiguration : IEntityTypeConfiguration<Pencil>
{
    public void Configure(EntityTypeBuilder<Pencil> builder) => builder.ToTable("pencils");
}

internal sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder) => builder.ToTable("books");
}

internal sealed record CreateCasesCommand(Guid PencilCaseId, Guid BookCaseId) : Command;

internal sealed class CreateCasesCommandHandler(IRepository<Case, CaseId> cases) : CommandHandler<CreateCasesCommand>
{
    public override async Task<Result> Handle(CreateCasesCommand command, CancellationToken cancellationToken)
    {
        await cases.AddAsync(new PencilCase(new CaseId(command.PencilCaseId)), cancellationToken);
        await cases.AddAsync(new BookCase(new CaseId(command.BookCaseId)), cancellationToken);
        return Result.Success();
    }
}

// Changes one child of each case in one Operation.
internal sealed record TouchCasesCommand(Guid PencilCaseId, Guid BookCaseId) : Command;

internal sealed class TouchCasesCommandHandler(IRepository<PencilCase, CaseId> pencilCases, IRepository<BookCase, CaseId> bookCases)
    : CommandHandler<TouchCasesCommand>
{
    public override async Task<Result> Handle(TouchCasesCommand command, CancellationToken cancellationToken)
    {
        (await pencilCases.FindAsync(new CaseId(command.PencilCaseId), cancellationToken))!.Sharpen();
        (await bookCases.FindAsync(new CaseId(command.BookCaseId), cancellationToken))!.Retitle();
        return Result.Success();
    }
}
