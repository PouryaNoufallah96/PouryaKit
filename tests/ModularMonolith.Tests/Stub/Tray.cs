using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

internal sealed record TrayId(Guid Value) : TypedId<Guid>(Value);

internal sealed record CardId(Guid Value) : TypedId<Guid>(Value);

internal sealed record PinId(Guid Value) : TypedId<Guid>(Value);

// An aggregate that is deleted when removed, with an auditable card on an optional key and a pin on a required key that
// is soft-deletable and auditable itself: what removing the tray does to each is decided before the save.
internal sealed class Tray : AggregateRoot<TrayId>
{
    private readonly List<Card> _cards = [];
    private readonly List<Pin> _pins = [];

    private Tray() { }

    public IReadOnlyList<Card> Cards => _cards;

    public IReadOnlyList<Pin> Pins => _pins;

    public static Tray Create(TrayId id)
    {
        var tray = new Tray { Id = id };
        tray._cards.Add(new Card(new CardId(Guid.CreateVersion7())));
        tray._pins.Add(new Pin(new PinId(Guid.CreateVersion7())));
        return tray;
    }

    public void PullPin() => _pins.Clear();
}

internal sealed class Card : Entity<CardId>, IAuditable
{
    private Card() { }

    public Card(CardId id) => Id = id;

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";
}

internal sealed class Pin : Entity<PinId>, IAuditable, ISoftDeletable
{
    private Pin() { }

    public Pin(PinId id) => Id = id;

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }
}

internal sealed class TrayConfiguration : IEntityTypeConfiguration<Tray>
{
    public void Configure(EntityTypeBuilder<Tray> builder)
    {
        builder.ToTable("trays");
        builder.HasMany(tray => tray.Cards).WithOne().IsRequired(false);
        builder.Navigation(tray => tray.Cards).AutoInclude();
        builder.HasMany(tray => tray.Pins).WithOne().IsRequired();
        builder.Navigation(tray => tray.Pins).AutoInclude();
    }
}

internal sealed class CardConfiguration : IEntityTypeConfiguration<Card>
{
    public void Configure(EntityTypeBuilder<Card> builder) => builder.ToTable("cards");
}

internal sealed class PinConfiguration : IEntityTypeConfiguration<Pin>
{
    public void Configure(EntityTypeBuilder<Pin> builder) => builder.ToTable("pins");
}

internal sealed record CreateTrayCommand(Guid TrayId) : Command;

internal sealed class CreateTrayCommandHandler(IRepository<Tray, TrayId> trays)
    : CommandHandler<CreateTrayCommand>
{
    public override async Task<Result> Handle(
        CreateTrayCommand command,
        CancellationToken cancellationToken
    )
    {
        await trays.AddAsync(Tray.Create(new TrayId(command.TrayId)), cancellationToken);
        return Result.Success();
    }
}

internal enum TrayEdit
{
    Remove,
    PullPin,
    PullPinThenRemove,
}

// Edits the tray and records every entry state the save itself changes to Added, Modified or Deleted, which a save
// whose changes were all prepared beforehand never does; accepting the saved changes afterwards is not recorded.
internal sealed record EditTrayCommand(Guid TrayId, TrayEdit Edit) : Command<ReactionLog>;

internal sealed class EditTrayCommandHandler(
    IRepository<Tray, TrayId> trays,
    StubDbContext dbContext,
    ReactionLog log
) : CommandHandler<EditTrayCommand, ReactionLog>
{
    public override async Task<Result<ReactionLog>> Handle(
        EditTrayCommand command,
        CancellationToken cancellationToken
    )
    {
        var saving = false;
        dbContext.SavingChanges += (_, _) => saving = true;
        dbContext.ChangeTracker.StateChanged += (_, changed) =>
        {
            if (
                saving
                && changed.NewState
                    is EntityState.Added
                        or EntityState.Modified
                        or EntityState.Deleted
            )
            {
                log.Add(
                    $"{changed.Entry.Metadata.ClrType.Name} {changed.OldState} -> {changed.NewState}"
                );
            }
        };

        var tray = (await trays.FindAsync(new TrayId(command.TrayId), cancellationToken))!;
        if (command.Edit is TrayEdit.PullPin or TrayEdit.PullPinThenRemove)
        {
            tray.PullPin();
        }

        if (command.Edit is TrayEdit.Remove or TrayEdit.PullPinThenRemove)
        {
            trays.Remove(tray);
        }

        return log;
    }
}
