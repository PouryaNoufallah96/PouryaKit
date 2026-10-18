namespace Ledger.Accounts.Features.GetAccount;

/// <summary>An account as its events fold it, with the number of events it holds.</summary>
internal sealed record AccountDto(Guid Id, string Holder, decimal Balance, bool IsActive, long Version);
