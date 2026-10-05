using Catalog.Products.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

// Aggregates whose Snapshots test what the Kit refuses or keeps apart: a shelf, with a composite string key, a list of
// labels changed in place and a complex size, whose mapper misbehaves when a label asks it to; a counter, whose key the
// database generates; and a leaflet, keyed by Catalog's product IDs, so a Snapshot the Stub declares for every aggregate
// keyed that way could reach Catalog's products too. Two declarations only a test's own Host makes: one for every desk
// item, which folders and trays both are, each mapped with keys of its own, and one of shelves made without a Module.

internal sealed class Shelf : AggregateRoot<string>
{
    private Shelf() { }

    public string Aisle { get; private set; } = "";

    public string Bay { get; private set; } = "";

    public List<string> Labels { get; private set; } = [];

    public ShelfSize Size { get; private set; } = new(1, 1);

    public static Shelf Put(string aisle, string bay, params string[] labels) =>
        new()
        {
            Aisle = aisle,
            Bay = bay,
            Labels = [.. labels],
        };

    public void AddLabel(string label) => Labels.Add(label);

    public void Resize(int width, int depth) => Size = new ShelfSize(width, depth);

    public void Announce() => Raise(new ShelfAnnouncedDomainEvent(Aisle, Bay));
}

internal sealed record ShelfSize(int Width, int Depth);

internal sealed record ShelfAnnouncedDomainEvent(string Aisle, string Bay) : DomainEvent;

internal sealed record ShelfSnapshotV1(string Aisle, string Bay, int Labels, bool IsRemoved, long Version);

internal sealed class ShelfSnapshotMapper(IDispatcher dispatcher) : ISnapshotMapper<Shelf, ShelfSnapshotV1>
{
    public const string AddsALabel = "mapper adds a label";
    public const string Resizes = "mapper resizes";
    public const string Announces = "mapper announces";
    public const string SendsAFailingCommand = "mapper sends a failing command";

    public ShelfSnapshotV1 Map(Shelf shelf)
    {
        if (shelf.Labels.Contains(SendsAFailingCommand))
        {
            // A mapper is synchronous; this one waits for the command, then ignores its Result.
            _ = dispatcher.SendAsync(new RefuseCommand(), CancellationToken.None).GetAwaiter().GetResult();
        }

        if (shelf.Labels.Contains(AddsALabel))
        {
            shelf.AddLabel("added by its mapper");
        }

        if (shelf.Labels.Contains(Resizes))
        {
            shelf.Resize(9, 9);
        }

        if (shelf.Labels.Contains(Announces))
        {
            shelf.Announce();
        }

        return new(shelf.Aisle, shelf.Bay, shelf.Labels.Count, IsRemoved: false, shelf.Version);
    }

    public ShelfSnapshotV1 Tombstone(Shelf shelf) => new(shelf.Aisle, shelf.Bay, shelf.Labels.Count, IsRemoved: true, shelf.Version);
}

internal sealed record PutShelvesCommand(string[][] Shelves) : Command;

// Each shelf is its aisle, its bay, then its labels.
internal sealed class PutShelvesCommandHandler(IRepository<Shelf, string> shelves) : CommandHandler<PutShelvesCommand>
{
    public override async Task<Result> Handle(PutShelvesCommand command, CancellationToken cancellationToken)
    {
        foreach (var shelf in command.Shelves)
        {
            await shelves.AddAsync(Shelf.Put(shelf[0], shelf[1], shelf[2..]), cancellationToken);
        }

        return Result.Success();
    }
}

internal sealed class ShelfConfiguration : IEntityTypeConfiguration<Shelf>
{
    public void Configure(EntityTypeBuilder<Shelf> builder)
    {
        builder.ToTable("shelves");
        builder.Ignore(shelf => shelf.Id);
        builder.HasKey(shelf => new { shelf.Aisle, shelf.Bay });
        builder.ComplexProperty(shelf => shelf.Size);
    }
}

internal sealed record CounterId(long Value) : TypedId<long>(Value);

internal sealed class Counter : AggregateRoot<CounterId>
{
    private Counter() { }

    public string Name { get; private set; } = "";

    public static Counter Start(string name) => new() { Name = name };
}

internal sealed record CounterSnapshotV1(long CounterId, bool IsRemoved, long Version);

internal sealed class CounterSnapshotMapper : ISnapshotMapper<Counter, CounterSnapshotV1>
{
    public CounterSnapshotV1 Map(Counter counter) => new(counter.Id.Value, IsRemoved: false, counter.Version);

    public CounterSnapshotV1 Tombstone(Counter counter) => new(counter.Id.Value, IsRemoved: true, counter.Version);
}

internal sealed record StartCounterCommand(string Name) : Command;

internal sealed class StartCounterCommandHandler(IRepository<Counter, CounterId> counters) : CommandHandler<StartCounterCommand>
{
    public override async Task<Result> Handle(StartCounterCommand command, CancellationToken cancellationToken)
    {
        await counters.AddAsync(Counter.Start(command.Name), cancellationToken);
        return Result.Success();
    }
}

internal sealed class CounterConfiguration : IEntityTypeConfiguration<Counter>
{
    public void Configure(EntityTypeBuilder<Counter> builder)
    {
        builder.ToTable("counters");
        builder.Property(counter => counter.Id).ValueGeneratedOnAdd();
    }
}

internal sealed class Leaflet : AggregateRoot<ProductId>
{
    private Leaflet() { }

    public string Title { get; private set; } = "";

    public static Leaflet Print(ProductId id, string title) => new() { Id = id, Title = title };
}

internal sealed record ProductKeyedSnapshotV1(string Aggregate, Guid ProductId, bool IsRemoved, long Version);

// Declared by the Stub for every aggregate keyed by a product ID: Catalog's products are, and must not publish it.
internal sealed class ProductKeyedSnapshotMapper : ISnapshotMapper<AggregateRoot<ProductId>, ProductKeyedSnapshotV1>
{
    public ProductKeyedSnapshotV1 Map(AggregateRoot<ProductId> aggregate) =>
        new(aggregate.GetType().Name, aggregate.Id.Value, IsRemoved: false, aggregate.Version);

    public ProductKeyedSnapshotV1 Tombstone(AggregateRoot<ProductId> aggregate) =>
        new(aggregate.GetType().Name, aggregate.Id.Value, IsRemoved: true, aggregate.Version);
}

internal sealed record PrintLeafletCommand(Guid ProductId, string Title) : Command;

internal sealed class PrintLeafletCommandHandler(IRepository<Leaflet, ProductId> leaflets) : CommandHandler<PrintLeafletCommand>
{
    public override async Task<Result> Handle(PrintLeafletCommand command, CancellationToken cancellationToken)
    {
        await leaflets.AddAsync(Leaflet.Print(new ProductId(command.ProductId), command.Title), cancellationToken);
        return Result.Success();
    }
}

internal sealed class LeafletConfiguration : IEntityTypeConfiguration<Leaflet>
{
    public void Configure(EntityTypeBuilder<Leaflet> builder)
    {
        builder.ToTable("leaflets");
        // Catalog's typed ID is not in the Stub's assembly, whose typed IDs alone are converted by convention.
        builder.Property(leaflet => leaflet.Id).HasConversion(id => id.Value, value => new ProductId(value));
    }
}

internal sealed class RecordSnapshotIntegrationEventHandler<TSnapshot>(IntegrationEventLog log, OperationContext context, Operation operation)
    : IIntegrationEventHandler<TSnapshot>
    where TSnapshot : class
{
    public Task<Result> Handle(TSnapshot snapshot, CancellationToken cancellationToken)
    {
        log.Add(new Received(snapshot, context, operation));
        return Task.FromResult(Result.Success());
    }
}

internal sealed record RefuseCommand : Command;

internal sealed class RefuseCommandHandler : CommandHandler<RefuseCommand>
{
    public static readonly Error Refused = Error.Conflict("Stub.Refused", "The command was refused.");

    public override Task<Result> Handle(RefuseCommand command, CancellationToken cancellationToken) => Task.FromResult<Result>(Refused);
}

internal interface IDeskItem : IAggregateRoot;

internal sealed record DeskItemSnapshotV1(string Aggregate, long Version);

internal sealed class DeskItemSnapshotMapper : ISnapshotMapper<IDeskItem, DeskItemSnapshotV1>
{
    public DeskItemSnapshotV1 Map(IDeskItem item) => new(item.GetType().Name, item.Version);

    public DeskItemSnapshotV1 Tombstone(IDeskItem item) => new(item.GetType().Name, item.Version);
}

internal sealed record MislaidShelfSnapshotV1(string Aisle, string Bay, long Version);

internal sealed class MislaidShelfSnapshotMapper : ISnapshotMapper<Shelf, MislaidShelfSnapshotV1>
{
    public MislaidShelfSnapshotV1 Map(Shelf shelf) => new(shelf.Aisle, shelf.Bay, shelf.Version);

    public MislaidShelfSnapshotV1 Tombstone(Shelf shelf) => new(shelf.Aisle, shelf.Bay, shelf.Version);
}
