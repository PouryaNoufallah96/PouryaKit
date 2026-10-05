using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Domain;

namespace ModularMonolith.Tests.Stub;

internal sealed record FolderId(Guid Value) : TypedId<Guid>(Value);

internal sealed record PageId(Guid Value) : TypedId<Guid>(Value);

// An aggregate with an owned part (its label, stored in its own row) and child entities (its pages, in a table of
// their own), so a change to either alone is a change of the aggregate. It is auditable and soft-deletable too.
internal sealed class Folder : AggregateRoot<FolderId>, IAuditable, ISoftDeletable
{
    private readonly List<Page> _pages = [];

    private Folder() { }

    public string Name { get; private set; } = "";

    public Label Label { get; private set; } = null!;

    public IReadOnlyList<Page> Pages => _pages;

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }

    public static Folder Create(FolderId id, string name, string colour, params string[] pages)
    {
        var folder = new Folder { Id = id, Name = name, Label = new Label(colour) };
        foreach (var text in pages)
        {
            folder.AddPage(text);
        }

        return folder;
    }

    public void Rename(string name)
    {
        Name = name;
        Raise(new FolderRenamedDomainEvent(Id));
    }

    public void Relabel(string colour) => Label = new Label(colour);

    public void AddPage(string text) => _pages.Add(new Page(new PageId(Guid.CreateVersion7()), text));

    public void RewritePage(string text, string newText) => _pages.Single(page => page.Text == text).Rewrite(newText);

    public void TearOutPage(string text) => _pages.Remove(_pages.Single(page => page.Text == text));
}

internal sealed class Label(string colour)
{
    public string Colour { get; private set; } = colour;
}

internal sealed class Page : Entity<PageId>
{
    private Page() { }

    public Page(PageId id, string text)
    {
        Id = id;
        Text = text;
    }

    public string Text { get; private set; } = "";

    public void Rewrite(string text) => Text = text;
}

internal sealed record FolderRenamedDomainEvent(FolderId FolderId) : DomainEvent;

internal sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.ToTable("folders");
        builder.OwnsOne(folder => folder.Label);
        builder.HasMany(folder => folder.Pages).WithOne().IsRequired();
        builder.Navigation(folder => folder.Pages).AutoInclude();
    }
}

internal sealed class PageConfiguration : IEntityTypeConfiguration<Page>
{
    public void Configure(EntityTypeBuilder<Page> builder) => builder.ToTable("pages");
}
