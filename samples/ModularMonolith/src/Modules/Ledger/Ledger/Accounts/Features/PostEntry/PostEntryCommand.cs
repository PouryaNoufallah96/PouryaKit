using PouryaKit.Cqrs;

namespace Ledger.Accounts.Features.PostEntry;

/// <summary>Posts an amount to an account: a credit when positive, a debit when negative.</summary>
internal sealed record PostEntryCommand(Guid AccountId, decimal Amount) : Command;
