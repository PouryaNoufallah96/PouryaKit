using Ledger.Accounts.ValueObjects;
using PouryaKit.Domain;

namespace Ledger.Accounts.Events.DomainEvents;

internal sealed record AccountActivatedDomainEvent(AccountId AccountId) : DomainEvent;
