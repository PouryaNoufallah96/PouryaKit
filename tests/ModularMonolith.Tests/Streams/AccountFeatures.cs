using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Marten;

namespace ModularMonolith.Tests.Streams;

internal static class AccountErrors
{
    public static readonly Error NotFound = Error.NotFound("Accounts.NotFound", "No such account.");
}

[Idempotent]
internal sealed record OpenAccountCommand(Guid AccountId, Guid Customer) : Command;

internal sealed class OpenAccountHandler(IStreams streams) : CommandHandler<OpenAccountCommand>
{
    public override async Task<Result> Handle(
        OpenAccountCommand command,
        CancellationToken cancellationToken
    )
    {
        await AccountProbe.PassAsync(command.AccountId);
        streams.Start(
            Account.Open(new AccountId(command.AccountId), new CustomerId(command.Customer))
        );
        return Result.Success();
    }
}

internal sealed record DepositCommand(Guid AccountId, decimal Amount) : Command;

internal sealed class DepositHandler(IStreams streams) : CommandHandler<DepositCommand>
{
    public override async Task<Result> Handle(
        DepositCommand command,
        CancellationToken cancellationToken
    )
    {
        if (
            await streams.LoadAsync<Account>(new AccountId(command.AccountId), cancellationToken)
            is not { } account
        )
        {
            return AccountErrors.NotFound;
        }

        await AccountProbe.PassAsync(command.AccountId);
        account.Deposit(command.Amount);
        return Result.Success();
    }
}

internal sealed record AccountView(
    Guid Id,
    Guid Customer,
    decimal Balance,
    bool IsWelcomed,
    long Version
);

internal sealed record GetAccountQuery(Guid AccountId) : Query<AccountView>;

internal sealed class GetAccountHandler(IStreams streams)
    : QueryHandler<GetAccountQuery, AccountView>
{
    public override async Task<Result<AccountView>> Handle(
        GetAccountQuery query,
        CancellationToken cancellationToken
    ) =>
        await streams.LoadAsync<Account>(new AccountId(query.AccountId), cancellationToken)
            is { } account
            ? new AccountView(
                account.Id,
                account.Customer!.Value,
                account.Balance,
                account.IsWelcomed,
                account.Version
            )
            : AccountErrors.NotFound;
}

// Changes a Stream in a query, which the reading-state guard refuses.
internal sealed record DepositInQuery(Guid AccountId) : Query<decimal>;

internal sealed class DepositInQueryHandler(IStreams streams)
    : QueryHandler<DepositInQuery, decimal>
{
    public override async Task<Result<decimal>> Handle(
        DepositInQuery query,
        CancellationToken cancellationToken
    )
    {
        var account = (
            await streams.LoadAsync<Account>(new AccountId(query.AccountId), cancellationToken)
        )!;
        account.Deposit(1);
        return account.Balance;
    }
}

// Points a tracked Stream at another stream in a query, which the reading-state guard refuses.
internal sealed record RedirectInQuery(Guid AccountId, Guid OtherId) : Query<bool>;

internal sealed class RedirectInQueryHandler(IStreams streams) : QueryHandler<RedirectInQuery, bool>
{
    public override async Task<Result<bool>> Handle(
        RedirectInQuery query,
        CancellationToken cancellationToken
    )
    {
        var account = (
            await streams.LoadAsync<Account>(new AccountId(query.AccountId), cancellationToken)
        )!;
        account.Id = query.OtherId;
        return true;
    }
}

// Sends DepositInQuery from a command and swallows its refusal: the Operation stays rollback-only, so nothing commits.
internal sealed record DepositThroughQueryCommand(Guid AccountId) : Command;

internal sealed class DepositThroughQueryHandler(IDispatcher dispatcher)
    : CommandHandler<DepositThroughQueryCommand>
{
    public override async Task<Result> Handle(
        DepositThroughQueryCommand command,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await dispatcher.QueryAsync(new DepositInQuery(command.AccountId), cancellationToken);
        }
        catch (InvalidOperationException) { }

        return Result.Success();
    }
}

// Deposits, then sends RedirectInQuery while the deposit is pending, and swallows its refusal.
internal sealed record DepositThenRedirectCommand(Guid AccountId, Guid OtherId) : Command;

internal sealed class DepositThenRedirectHandler(IStreams streams, IDispatcher dispatcher)
    : CommandHandler<DepositThenRedirectCommand>
{
    public override async Task<Result> Handle(
        DepositThenRedirectCommand command,
        CancellationToken cancellationToken
    )
    {
        (
            await streams.LoadAsync<Account>(new AccountId(command.AccountId), cancellationToken)
        )!.Deposit(5);
        try
        {
            await dispatcher.QueryAsync(
                new RedirectInQuery(command.AccountId, command.OtherId),
                cancellationToken
            );
        }
        catch (InvalidOperationException) { }

        return Result.Success();
    }
}

// An idempotent command that changes no Stream: its idempotency key is the Operation's only write.
[Idempotent]
internal sealed record PingCommand(Guid Key) : Command<Guid>;

internal sealed class PingHandler : CommandHandler<PingCommand, Guid>
{
    public override Task<Result<Guid>> Handle(
        PingCommand command,
        CancellationToken cancellationToken
    )
    {
        AccountProbe.Ran(command.Key);
        return Task.FromResult<Result<Guid>>(Guid.CreateVersion7());
    }
}
