using System.Reflection;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.EFCore;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Offending;

// Each type breaks one structure rule, so the rule is seen to reject it. Every offence sits inside an async method or a
// lambda, which the compiler moves into a type of its own, so a rule that reads only the declared methods misses it.

// Its project references Catalog's implementation, not only its Contracts, though no code here uses it.
public sealed class OffendingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) => module.AddScoped<SavingCommandHandler>();
}

internal sealed record OffendCommand : Command;

internal sealed class SavingCommandHandler(OffendingDbContext dbContext) : CommandHandler<OffendCommand>
{
    public override async Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

internal sealed class CommittingCommandHandler(IUnitOfWorkConnection unitOfWork) : CommandHandler<OffendCommand>
{
    public override async Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        var (connection, _) = unitOfWork.Join();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("commit", cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("select 1;\n  END ;", cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("update offending_marks set seen = true; abort work", cancellationToken: cancellationToken));
        return Result.Success();
    }
}

// Writes rows at once, outside the changes the Unit of Work tracks and prepares.
internal sealed class BulkWritingCommandHandler(OffendingDbContext dbContext) : CommandHandler<OffendCommand>
{
    public override async Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        await dbContext.Set<OffendingRow>().ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "renamed"), cancellationToken);
        Func<int> forget = () => dbContext.Set<OffendingRow>().ExecuteDelete();
        return forget() > 0 ? Result.Success() : Result.Success();
    }
}

internal sealed class OffendingRow
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
}

// Calls the steps only the Kit runs: the Unit of Work and its DbContext, the Outbox write, a Receipt, a Snapshot mapping, an Integration Event and a delayed
// command added to the Outbox, an idempotency key reserved and its success recorded, the Domain Event drain, a new Operation begun in its own scope, a
// reading step, and a query cache eviction.
internal sealed class KitStepCallingCommandHandler(
    IUnitOfWork unitOfWork,
    IUnitOfWorkOutbox outbox,
    IReceipts receipts,
    ISnapshotMappers snapshots,
    IIntegrationEventOutbox integrationEvents,
    IDelayedCommandOutbox delayedCommands,
    IIdempotencyStore idempotency,
    DomainEventDrain drain,
    IServiceProvider services,
    Operation operation,
    IReadingStateGuard readingState,
    IQueryCache queryCache,
    OffendingDbContext dbContext
) : CommandHandler<OffendCommand>
{
    public override async Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        await unitOfWork.RunAsync(_ => Task.FromResult(Result.Success()), cancellationToken);
        _ = EfUnitOfWorkDbContext.Of(unitOfWork);
        await outbox.WriteAsync(dbContext, [command], [], cancellationToken);
        await receipts.RecordAsync("offending", Guid.CreateVersion7(), cancellationToken);
        _ = snapshots.Map(command, removed: false);
        integrationEvents.Add(Guid.CreateVersion7(), command);
        delayedCommands.Add(command, DateTimeOffset.UnixEpoch);
        var key = new IdempotencyKey(null, Actor.System("offending"), nameof(OffendCommand), "offending");
        _ = await idempotency.ReserveAsync(key, cancellationToken);
        await idempotency.RecordAsync(key, "{}", cancellationToken);
        await drain.RunAsync(() => [], cancellationToken);
        services.BeginOperation(new OperationContext(Actor.System("offending")));
        using var reading = operation.OnlyReading("offending", compareTrackedState: false);
        using var guarded = readingState.OnlyReading("offending");
        using var tracked = TrackedState.Capture(dbContext);
        tracked.ThrowIfChanged("offending");
        await queryCache.EvictAsync(["offending"]);
        return Result.Success();
    }
}

// Fills the dispatcher's query cache slot itself: a pipeline step by another name.
internal sealed class OwnQueryCache : IQueryCache
{
    public Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        OperationContext context,
        Func<CancellationToken, Task<Result<TResponse>>> handle,
        Action endRead,
        CancellationToken cancellationToken
    )
        where TResponse : notnull => handle(cancellationToken);

    public IEnumerable<string> TagsInvalidatedBy(object command) => [];

    public Task EvictAsync(IReadOnlyCollection<string> tags) => Task.CompletedTask;
}

internal sealed class ConnectingCommandHandler(OffendingDbContext dbContext) : CommandHandler<OffendCommand>
{
    public override Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        Func<DbContext, bool> canConnect = context => context.Database.CanConnect();
        return Task.FromResult(canConnect(dbContext) ? Result.Success() : Result.Success());
    }
}

internal sealed class PublishingCommandHandler(IMessageBus bus) : CommandHandler<OffendCommand>
{
    public override async Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        await bus.PublishAsync(command);
        return Result.Success();
    }
}

// Maps the message store's envelopes into the Module's own model.
internal sealed class OffendingDbContext(DbContextOptions options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.MapWolverineEnvelopeStorage("wolverine");
}

internal sealed record OffendQuery : Query<int>;

// Reads Catalog's tenant-scoped tables, each time naming tenant = @Tenant, in shapes that still cross tenants: the
// predicate inside a top-level OR, in one branch of a UNION, and for one table of a join but not the other.
internal sealed class CrossTenantReadingQueryHandler(IReadConnectionFactory connections, OperationContext context)
    : QueryHandler<OffendQuery, int>
{
    public override async Task<Result<int>> Handle(OffendQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var parameters = new { context.Tenant, Name = "Lamp" };
        var either = await connection.QueryAsync<Guid>(
            new CommandDefinition("select id from catalog.products where tenant = @Tenant or name = @Name", parameters, cancellationToken: cancellationToken)
        );
        var united = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                "select id from catalog.products where tenant = @Tenant union select id from catalog.products where name = @Name",
                parameters,
                cancellationToken: cancellationToken
            )
        );
        var joined = await connection.QueryAsync<Guid>(
            new CommandDefinition(
                "select p.id from catalog.products p join catalog.categories c on c.id = p.category_id where p.tenant = @Tenant and c.name = @Name",
                parameters,
                cancellationToken: cancellationToken
            )
        );
        return either.Count() + united.Count() + joined.Count();
    }
}

// A tenant's ledger, whose entries and their lines are parts its navigations hold, so their tables carry no tenant.
internal sealed record LedgerId(Guid Value) : TypedId<Guid>(Value);

internal sealed record LedgerEntryId(Guid Value) : TypedId<Guid>(Value);

internal sealed record EntryLineId(Guid Value) : TypedId<Guid>(Value);

internal sealed class Ledger : AggregateRoot<LedgerId>, ITenantScoped
{
    private readonly List<LedgerEntry> _entries = [];

    private Ledger() { }

    public string Tenant { get; private set; } = "";

    public string Name { get; private set; } = "";

    public IReadOnlyList<LedgerEntry> Entries => _entries;
}

internal sealed class LedgerEntry : Entity<LedgerEntryId>
{
    private readonly List<EntryLine> _lines = [];

    private LedgerEntry() { }

    public decimal Amount { get; private set; }

    public IReadOnlyList<EntryLine> Lines => _lines;
}

internal sealed class EntryLine : Entity<EntryLineId>
{
    private EntryLine() { }

    public string Text { get; private set; } = "";
}

internal sealed class LedgerDbContext(DbContextOptions options) : PouryaKitDbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Ledger>().ToTable("ledgers").HasMany(ledger => ledger.Entries).WithOne().IsRequired();
        modelBuilder.Entity<LedgerEntry>().ToTable("ledger_entries").HasMany(entry => entry.Lines).WithOne().IsRequired();
        modelBuilder.Entity<EntryLine>().ToTable("entry_lines");
    }
}

// Reads a ledger's entries, whose table has no tenant column: once with no join to the ledger, and once joined to it
// without holding the ledger to the tenant.
internal sealed class PartReadingQueryHandler(IReadConnectionFactory connections) : QueryHandler<OffendQuery, int>
{
    public override async Task<Result<int>> Handle(OffendQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var parameters = new { Ledger = Guid.Empty, Name = "Cash" };
        var direct = await connection.QueryAsync<decimal>(
            new CommandDefinition("select amount from offending.ledger_entries where ledger_id = @Ledger", parameters, cancellationToken: cancellationToken)
        );
        var joined = await connection.QueryAsync<decimal>(
            new CommandDefinition(
                "select e.amount from offending.ledger_entries e join offending.ledgers l on l.id = e.ledger_id where l.name = @Name",
                parameters,
                cancellationToken: cancellationToken
            )
        );
        return direct.Count() + joined.Count();
    }
}

// Writes Catalog's aggregate tables around the tracked path, once named with its schema and once without.
internal sealed class RenamingProductsCommandHandler(IUnitOfWorkConnection unitOfWork) : CommandHandler<OffendCommand>
{
    private static readonly string ForgetCategories = "delete from categories";

    public override async Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition("update catalog.products set name = 'renamed'", transaction: transaction, cancellationToken: cancellationToken)
        );
        await connection.ExecuteAsync(new CommandDefinition(ForgetCategories, transaction: transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(
            new CommandDefinition("update catalog\n.\tproducts set name = 'renamed again'", transaction: transaction, cancellationToken: cancellationToken)
        );
        return Result.Success();
    }
}

internal sealed record OffendingHappenedDomainEvent : DomainEvent;

internal sealed class ClearingDomainEventHandler(IAggregateRoot aggregate) : IDomainEventHandler<OffendingHappenedDomainEvent>
{
    public Task<Result> Handle(OffendingHappenedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        Action<IAggregateRoot> clear = raised => raised.ClearDomainEvents();
        clear(aggregate);
        return Task.FromResult(Result.Success());
    }
}

// Writes its typed IDs in JSON of its own: snake_case names beside the Kit's camelCase.
internal static class OwnJson
{
    public static JsonSerializerOptions Options() =>
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
}

// Public, so other Modules could reach it; the generated migration beside it is public too, and that is no offence.
public sealed record ExposedQuery : Query<int>;

public sealed class ExposedQueryHandler : QueryHandler<ExposedQuery, int>
{
    public override Task<Result<int>> Handle(ExposedQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<int>>(1);
}

// Lists an assembly's types itself instead of asking PouryaKit.Core's ImplementingTypes.
internal static class OwnTypeLookup
{
    public static IEnumerable<Type> All(Assembly assembly)
    {
        Func<Module, IEnumerable<Type>> inModule = module => module.GetTypes().Concat(module.FindTypes(null, null));
        return assembly
            .GetTypes()
            .Concat(assembly.DefinedTypes)
            .Concat(assembly.ExportedTypes)
            .Concat(assembly.GetExportedTypes())
            .Concat(assembly.Modules.SelectMany(inModule))
            .Concat(assembly.GetModules().SelectMany(inModule));
    }
}

[Migration("20261006000000_OffendingInitial")]
public partial class OffendingInitial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
}
