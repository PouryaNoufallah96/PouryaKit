using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
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

// Calls the steps only the Kit runs: the Unit of Work and its DbContext, the Outbox write, a Receipt, a Snapshot mapping, an Integration Event added to the
// Outbox, the Domain Event drain, a new Operation begun in its own scope, and a reading step.
internal sealed class KitStepCallingCommandHandler(
    IUnitOfWork unitOfWork,
    IUnitOfWorkOutbox outbox,
    IReceipts receipts,
    ISnapshotMappers snapshots,
    IIntegrationEventOutbox integrationEvents,
    DomainEventDrain drain,
    IServiceProvider services,
    Operation operation,
    OffendingDbContext dbContext
) : CommandHandler<OffendCommand>
{
    public override async Task<Result> Handle(OffendCommand command, CancellationToken cancellationToken)
    {
        await unitOfWork.RunAsync(_ => Task.FromResult(Result.Success()), cancellationToken);
        _ = unitOfWork.DbContext;
        await outbox.WriteAsync(dbContext, [command], [], cancellationToken);
        await receipts.RecordAsync("offending", Guid.CreateVersion7(), cancellationToken);
        _ = snapshots.Map(command, removed: false);
        integrationEvents.Add(Guid.CreateVersion7(), command);
        await drain.RunAsync(() => [], cancellationToken);
        services.BeginOperation(new OperationContext(Actor.System("offending")));
        using var reading = operation.OnlyReading("offending");
        return Result.Success();
    }
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

// Public, so other Modules could reach it; the generated migration beside it is public too, and that is no offence.
public sealed record ExposedQuery : Query<int>;

public sealed class ExposedQueryHandler : QueryHandler<ExposedQuery, int>
{
    public override Task<Result<int>> Handle(ExposedQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<Result<int>>(1);
}

[Migration("20261006000000_OffendingInitial")]
public partial class OffendingInitial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) { }
}
