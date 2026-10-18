using PouryaKit.Cqrs;

namespace Ledger.Accounts.Features.OpenAccount;

/// <summary>Opens an account for a holder; returns its id. An account opened twice is a concurrency conflict.</summary>
internal sealed record OpenAccountCommand(Guid AccountId, string Holder) : Command<Guid>;
