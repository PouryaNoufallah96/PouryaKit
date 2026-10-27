using Ledger.Accounts.Events.DomainEvents;
using Ledger.Accounts.ValueObjects;
using PouryaKit.Persistence.Marten;

namespace Ledger.Accounts.Entities;

#region account-stream
// A Stream: every behaviour applies its event, then raises it for the Unit of Work to append, and Marten folds the
// stored events through the same Apply methods, which are public because its source generator dispatches only to public
// ones. Marten stores an event under its type's name without the namespace (account_opened_domain_event), so an event
// type's name stays unique among the Stream Modules of the Host's one store.
internal sealed class Account : StreamAggregate
{
    public string Holder { get; private set; } = "";

    public decimal Balance { get; private set; }

    /// <summary>Whether the other Modules were told of the account, which its activation records.</summary>
    public bool IsActive { get; private set; }

    public static Account Open(AccountId id, string holder)
    {
        var account = new Account();
        var opened = new AccountOpenedDomainEvent(id, holder);
        account.Apply(opened);
        account.Raise(opened);
        return account;
    }

    public void Post(decimal amount)
    {
        var posted = new EntryPostedDomainEvent(new AccountId(Id), amount);
        Apply(posted);
        Raise(posted);
    }

    public void Activate()
    {
        var activated = new AccountActivatedDomainEvent(new AccountId(Id));
        Apply(activated);
        Raise(activated);
    }

    public void Apply(AccountOpenedDomainEvent opened)
    {
        Id = opened.AccountId.Value;
        Holder = opened.Holder;
    }

    public void Apply(EntryPostedDomainEvent posted) => Balance += posted.Amount;

    public void Apply(AccountActivatedDomainEvent activated) => IsActive = true;
}
#endregion
