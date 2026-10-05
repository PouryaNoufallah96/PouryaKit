using Ardalis.Specification;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Stub;

// A long typed ID whose value the database generates.
internal sealed record NoteId(long Value) : TypedId<long>(Value);

internal sealed class Note : AggregateRoot<NoteId>
{
    private Note() { }

    public string Text { get; private set; } = "";

    public static Note Write(string text) => new() { Text = text };

    // The Stub's Domain Events exist only to drive the drain, so a note raises whichever one a test needs.
    public Note Raising(DomainEvent domainEvent)
    {
        Raise(domainEvent);
        return this;
    }
}

internal sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("notes");
        // Conventions never make a key with a value converter database-generated; a numeric typed ID that the
        // database generates says so here.
        builder.Property(note => note.Id).ValueGeneratedOnAdd();
    }
}

internal sealed class NotesWithText : Specification<Note>
{
    public NotesWithText(string text) => Query.Where(note => note.Text == text).OrderBy(note => note.Id);
}

internal sealed class StubDbContext(DbContextOptions<StubDbContext> options) : PouryaKitDbContext(options);
