using PouryaKit.Domain;

namespace Ledger.Accounts.ValueObjects;

internal sealed record AccountId(Guid Value) : TypedId<Guid>(Value);
