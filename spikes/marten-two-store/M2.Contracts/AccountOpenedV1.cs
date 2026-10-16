namespace M2.Contracts;

/// <summary>Ledger's Integration Event: an account was opened and welcomed. Catalog subscribes to it.</summary>
public sealed record AccountOpenedV1(Guid AccountId, Guid Customer);
