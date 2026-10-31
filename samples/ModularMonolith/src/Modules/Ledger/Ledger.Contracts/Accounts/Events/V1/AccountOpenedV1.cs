namespace Ledger.Contracts.Accounts.Events.V1;

/// <summary>
/// An account was opened in the ledger for <paramref name="Holder"/>. <paramref name="Id"/> and
/// <paramref name="OccurredOn"/> are the identity and the time of the Domain Event it follows from, the same in every
/// delivery. Ledger publishes it under the wire name <c>ledger.account-opened.v1</c>.
/// </summary>
public sealed record AccountOpenedV1(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid AccountId,
    string Holder
);
