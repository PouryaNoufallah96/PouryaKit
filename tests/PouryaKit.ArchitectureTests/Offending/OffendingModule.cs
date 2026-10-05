using Catalog;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Offending;

// Each type breaks one structure rule, so the rule is seen to reject it. Every offence sits inside an async method or a
// lambda, which the compiler moves into a type of its own, so a rule that reads only the declared methods misses it.

// Knows Catalog's implementation, not only its Contracts.
public sealed class OffendingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) => module.AddSingleton(typeof(CatalogModule));
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
