using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

/// <summary>Where a held Operation waits: it reports that it has made its change, then waits to be let go.</summary>
internal sealed class Hold(string? mark = null)
{
    /// <summary>
    /// The name of the category the Operation writes with plain SQL, which only a commit keeps; null to write none.
    /// </summary>
    public string? Mark { get; } = mark;

    public TaskCompletionSource Changed { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Released { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

// Begun in a scope as a Catalog Operation, it runs a Catalog Feature's command nested, so the Feature loads and changes
// its aggregates in this Operation's Unit of Work, then waits before the Unit of Work saves: two of them running at once
// have both read the same version before either commits. It also writes a category of the Operation's tenant straight to
// the database in the Operation's transaction, stating the tenant itself, as plain SQL must: the column has no default. EF
// Core undoes its own writes when a save fails, so only this row shows whether the Unit of Work rolled the transaction back.
internal sealed record HoldChangeCommand(
    Func<IDispatcher, CancellationToken, Task<Result>> Change,
    Hold Hold
) : Command;

internal sealed class HoldChangeCommandHandler(
    IDispatcher dispatcher,
    IUnitOfWorkConnection unitOfWork,
    OperationContext context
) : CommandHandler<HoldChangeCommand>
{
    public override async Task<Result> Handle(
        HoldChangeCommand command,
        CancellationToken cancellationToken
    )
    {
        var changed = await command.Change(dispatcher, cancellationToken);
        if (command.Hold.Mark is not null)
        {
            var (connection, transaction) = unitOfWork.Join();
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "insert into catalog.categories (id, name, product_count, version, tenant) values (@Id, @Mark, 0, 1, @Tenant)",
                    new
                    {
                        Id = Guid.CreateVersion7(),
                        command.Hold.Mark,
                        context.Tenant,
                    },
                    transaction,
                    cancellationToken: cancellationToken
                )
            );
        }
        command.Hold.Changed.SetResult();
        await command.Hold.Released.Task.WaitAsync(cancellationToken);
        return changed;
    }
}
