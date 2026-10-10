using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.ValueGeneration;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

// Aggregates with a Snapshot whose mapper reads a value the database generates when the aggregate is saved, after the
// Snapshot is built: a ticket, whose serial is a default the database fills in, and a drawer, whose knob, an owned part,
// has a label the database computes from its finish. Beside them a badge, whose code a client-side generator sets for
// good when the badge is added, before the Snapshot is built.

internal sealed record TicketId(Guid Value) : TypedId<Guid>(Value);

internal sealed class Ticket : AggregateRoot<TicketId>
{
    private Ticket() { }

    public string? Serial { get; private set; }

    public static Ticket Issue(TicketId id) => new() { Id = id };
}

internal sealed record TicketSnapshotV1(Guid TicketId, string? Serial, bool IsRemoved, long Version);

internal sealed class TicketSnapshotMapper : ISnapshotMapper<Ticket, TicketSnapshotV1>
{
    public TicketSnapshotV1 Map(Ticket ticket) => new(ticket.Id.Value, ticket.Serial, IsRemoved: false, ticket.Version);

    public TicketSnapshotV1 Tombstone(Ticket ticket) => new(ticket.Id.Value, ticket.Serial, IsRemoved: true, ticket.Version);
}

internal sealed record IssueTicketCommand(Guid Ticket) : Command;

internal sealed class IssueTicketCommandHandler(IRepository<Ticket, TicketId> tickets) : CommandHandler<IssueTicketCommand>
{
    public override async Task<Result> Handle(IssueTicketCommand command, CancellationToken cancellationToken)
    {
        await tickets.AddAsync(Ticket.Issue(new TicketId(command.Ticket)), cancellationToken);
        return Result.Success();
    }
}

internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    public const string Serial = "printed by the database";

    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");
        builder.Property(ticket => ticket.Serial).HasDefaultValueSql($"'{Serial}'");
    }
}

internal sealed record DrawerId(Guid Value) : TypedId<Guid>(Value);

internal sealed class Drawer : AggregateRoot<DrawerId>
{
    private Drawer() { }

    public Knob Knob { get; private set; } = null!;

    public static Drawer Fit(DrawerId id, string finish) => new() { Id = id, Knob = new Knob(finish) };
}

internal sealed class Knob(string finish)
{
    public string Finish { get; private set; } = finish;

    public string? Label { get; private set; }
}

internal sealed record DrawerSnapshotV1(Guid DrawerId, string? KnobLabel, bool IsRemoved, long Version);

internal sealed class DrawerSnapshotMapper : ISnapshotMapper<Drawer, DrawerSnapshotV1>
{
    public DrawerSnapshotV1 Map(Drawer drawer) => new(drawer.Id.Value, drawer.Knob.Label, IsRemoved: false, drawer.Version);

    public DrawerSnapshotV1 Tombstone(Drawer drawer) => new(drawer.Id.Value, drawer.Knob.Label, IsRemoved: true, drawer.Version);
}

internal sealed record FitDrawerCommand(Guid Drawer, string Finish) : Command;

internal sealed class FitDrawerCommandHandler(IRepository<Drawer, DrawerId> drawers) : CommandHandler<FitDrawerCommand>
{
    public override async Task<Result> Handle(FitDrawerCommand command, CancellationToken cancellationToken)
    {
        await drawers.AddAsync(Drawer.Fit(new DrawerId(command.Drawer), command.Finish), cancellationToken);
        return Result.Success();
    }
}

internal sealed class DrawerConfiguration : IEntityTypeConfiguration<Drawer>
{
    public void Configure(EntityTypeBuilder<Drawer> builder)
    {
        builder.ToTable("drawers");
        builder.OwnsOne(drawer => drawer.Knob, knob => knob.Property(part => part.Label).HasComputedColumnSql("upper(knob_finish)", stored: true));
    }
}

internal sealed record BadgeId(Guid Value) : TypedId<Guid>(Value);

internal sealed class Badge : AggregateRoot<BadgeId>
{
    private Badge() { }

    public Guid Code { get; private set; }

    public static Badge Issue(BadgeId id) => new() { Id = id };
}

internal sealed record BadgeSnapshotV1(Guid BadgeId, Guid Code, bool IsRemoved, long Version);

internal sealed class BadgeSnapshotMapper : ISnapshotMapper<Badge, BadgeSnapshotV1>
{
    public BadgeSnapshotV1 Map(Badge badge) => new(badge.Id.Value, badge.Code, IsRemoved: false, badge.Version);

    public BadgeSnapshotV1 Tombstone(Badge badge) => new(badge.Id.Value, badge.Code, IsRemoved: true, badge.Version);
}

internal sealed record IssueBadgeCommand(Guid Badge) : Command;

internal sealed class IssueBadgeCommandHandler(IRepository<Badge, BadgeId> badges) : CommandHandler<IssueBadgeCommand>
{
    public override async Task<Result> Handle(IssueBadgeCommand command, CancellationToken cancellationToken)
    {
        await badges.AddAsync(Badge.Issue(new BadgeId(command.Badge)), cancellationToken);
        return Result.Success();
    }
}

internal sealed class BadgeConfiguration : IEntityTypeConfiguration<Badge>
{
    public void Configure(EntityTypeBuilder<Badge> builder)
    {
        builder.ToTable("badges");
        builder.Property(badge => badge.Code).ValueGeneratedOnAdd().HasValueGenerator<GuidValueGenerator>();
    }
}
