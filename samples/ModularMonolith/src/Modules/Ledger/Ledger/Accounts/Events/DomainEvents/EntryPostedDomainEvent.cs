using Ledger.Accounts.ValueObjects;
using PouryaKit.Domain;

namespace Ledger.Accounts.Events.DomainEvents;

internal sealed record EntryPostedDomainEvent(AccountId AccountId, decimal Amount) : DomainEvent;
