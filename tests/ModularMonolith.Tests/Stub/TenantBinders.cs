using Catalog.Infrastructure.Data.Context;
using Catalog.Products.Entities;
using Catalog.Products.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Stub;

internal sealed record BinderId(Guid Value) : TypedId<Guid>(Value);

internal sealed record SheetId(Guid Value) : TypedId<Guid>(Value);

// A tenant's aggregate, deleted outright when removed, with sheets on a required key: a changed sheet, a sheet moved to
// another binder and the sheets a removal deletes with it are each a change of the binder that holds, or held, it.
internal sealed class Binder : AggregateRoot<BinderId>, ITenantScoped
{
    private readonly List<Sheet> _sheets = [];

    private Binder() { }

    public string Tenant { get; private set; } = "";

    public string Name { get; private set; } = "";

    public IReadOnlyList<Sheet> Sheets => _sheets;

    public static Binder Create(BinderId id, string tenant, string name, params string[] sheets)
    {
        var binder = new Binder { Id = id, Tenant = tenant, Name = name };
        binder._sheets.AddRange(sheets.Select(text => new Sheet(new SheetId(Guid.CreateVersion7()), text)));
        return binder;
    }

    public void Rename(string name) => Name = name;

    public void RewriteSheet(string newText) => _sheets[0].Rewrite(newText);

    public void MoveSheet(Binder other)
    {
        var sheet = _sheets[0];
        _sheets.Remove(sheet);
        other._sheets.Add(sheet);
    }
}

internal sealed class Sheet : Entity<SheetId>
{
    private Sheet() { }

    public Sheet(SheetId id, string text)
    {
        Id = id;
        Text = text;
    }

    public string Text { get; private set; } = "";

    public void Rewrite(string text) => Text = text;
}

// A tenant's aggregate keyed by its tenant and its own code, which another tenant's docket may share.
internal sealed class Docket : AggregateRoot<string>, ITenantScoped
{
    private Docket() { }

    public string Tenant { get; private set; } = "";
}

internal sealed record DocketSnapshotV1(string Tenant, string Code, bool IsRemoved, long Version);

internal sealed class DocketSnapshotMapper : ISnapshotMapper<Docket, DocketSnapshotV1>
{
    public DocketSnapshotV1 Map(Docket aggregate) => new(aggregate.Tenant, aggregate.Id, false, aggregate.Version);

    public DocketSnapshotV1 Tombstone(Docket aggregate) => new(aggregate.Tenant, aggregate.Id, true, aggregate.Version);
}

internal sealed class BinderConfiguration : IEntityTypeConfiguration<Binder>
{
    public void Configure(EntityTypeBuilder<Binder> builder)
    {
        builder.ToTable("binders");
        builder.HasMany(binder => binder.Sheets).WithOne().IsRequired();
        builder.Navigation(binder => binder.Sheets).AutoInclude();
    }
}

internal sealed class SheetConfiguration : IEntityTypeConfiguration<Sheet>
{
    public void Configure(EntityTypeBuilder<Sheet> builder) => builder.ToTable("sheets");
}

internal sealed class DocketConfiguration : IEntityTypeConfiguration<Docket>
{
    public void Configure(EntityTypeBuilder<Docket> builder)
    {
        builder.ToTable("dockets");
        builder.HasKey(docket => new { docket.Tenant, docket.Id });
    }
}

// Adds a binder for the tenant named, the Operation's when none is.
internal sealed record CreateBinderCommand(Guid BinderId, string Name, string[] Sheets, string? Tenant = null) : Command;

internal sealed class CreateBinderCommandHandler(IRepository<Binder, BinderId> binders, OperationContext context)
    : CommandHandler<CreateBinderCommand>
{
    public override async Task<Result> Handle(CreateBinderCommand command, CancellationToken cancellationToken)
    {
        await binders.AddAsync(
            Binder.Create(new BinderId(command.BinderId), command.Tenant ?? context.Tenant!, command.Name, command.Sheets),
            cancellationToken
        );
        return Result.Success();
    }
}

public enum BinderChange
{
    Rename,
    RewriteSheet,
    MoveSheet,
    Remove,
}

// Loads both binders with the tenant filter set aside, whichever tenant they belong to, and changes the first; a moved
// sheet goes from the first to the second.
internal sealed record ChangeBinderCommand(Guid BinderId, Guid OtherId, BinderChange Change) : Command;

internal sealed class ChangeBinderCommandHandler(StubDbContext dbContext) : CommandHandler<ChangeBinderCommand>
{
    public override async Task<Result> Handle(ChangeBinderCommand command, CancellationToken cancellationToken)
    {
        var binders = await dbContext
            .Set<Binder>()
            .IgnoreQueryFilters([PouryaKitDbContext.TenantFilter])
            .Where(binder => binder.Id == new BinderId(command.BinderId) || binder.Id == new BinderId(command.OtherId))
            .ToListAsync(cancellationToken);
        var binder = binders.Single(binder => binder.Id.Value == command.BinderId);
        switch (command.Change)
        {
            case BinderChange.Rename:
                binder.Rename("Renamed");
                break;
            case BinderChange.RewriteSheet:
                binder.RewriteSheet("Rewritten");
                break;
            case BinderChange.MoveSheet:
                binder.MoveSheet(binders.Single(other => other.Id.Value == command.OtherId));
                break;
            case BinderChange.Remove:
                dbContext.Remove(binder);
                break;
        }

        return Result.Success();
    }
}

// Loads a binder with the tenant filter set aside and leaves it unchanged, then writes a note, which is neither
// tenant-scoped nor auditable.
internal sealed record WriteNoteBesideBinderCommand(Guid BinderId, string Text) : Command;

internal sealed class WriteNoteBesideBinderCommandHandler(StubDbContext dbContext, IRepository<Note, NoteId> notes)
    : CommandHandler<WriteNoteBesideBinderCommand>
{
    public override async Task<Result> Handle(WriteNoteBesideBinderCommand command, CancellationToken cancellationToken)
    {
        await dbContext
            .Set<Binder>()
            .IgnoreQueryFilters([PouryaKitDbContext.TenantFilter])
            .SingleAsync(binder => binder.Id == new BinderId(command.BinderId), cancellationToken);
        await notes.AddAsync(Note.Write(command.Text), cancellationToken);
        return Result.Success();
    }
}

// Run in a Catalog Operation: loads a product of any tenant with the tenant filter set aside, gives it the tenant named,
// as no Feature of Catalog does, then renames it or removes it, which keeps it marked deleted.
internal sealed record ReassignProductCommand(Guid ProductId, string Tenant, bool Remove) : Command;

internal sealed class ReassignProductCommandHandler(CatalogDbContext dbContext) : CommandHandler<ReassignProductCommand>
{
    public override async Task<Result> Handle(ReassignProductCommand command, CancellationToken cancellationToken)
    {
        var product = await dbContext
            .Set<Product>()
            .IgnoreQueryFilters([PouryaKitDbContext.TenantFilter])
            .SingleAsync(product => product.Id == new ProductId(command.ProductId), cancellationToken);
        dbContext.Entry(product).Property(product => product.Tenant).CurrentValue = command.Tenant;
        if (command.Remove)
        {
            dbContext.Remove(product);
        }
        else
        {
            product.Rename("Reassigned");
        }

        return Result.Success();
    }
}
