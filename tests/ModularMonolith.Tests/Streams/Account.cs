using PouryaKit.Domain;
using PouryaKit.Persistence.Marten;

namespace ModularMonolith.Tests.Streams;

internal sealed record AccountId(Guid Value) : TypedId<Guid>(Value);

internal sealed record CustomerId(Guid Value) : TypedId<Guid>(Value);

internal sealed record AccountOpened(Guid AccountId, CustomerId Customer) : DomainEvent;

internal sealed record Deposited(Guid AccountId, decimal Amount) : DomainEvent;

internal sealed record Welcomed(Guid AccountId) : DomainEvent;

// A Stream: every behaviour applies its event and raises it, and Marten folds the stored events through the same Apply
// methods. The type is internal; Marten's source generator dispatches only to public Apply methods.
internal sealed class Account : StreamAggregate
{
    public CustomerId? Customer { get; private set; }

    public decimal Balance { get; private set; }

    public bool IsWelcomed { get; private set; }

    public static Account Open(AccountId id, CustomerId customer)
    {
        var account = new Account();
        var opened = new AccountOpened(id.Value, customer);
        account.Apply(opened);
        account.Raise(opened);
        return account;
    }

    public void Deposit(decimal amount)
    {
        var deposited = new Deposited(Id, amount);
        Apply(deposited);
        Raise(deposited);
    }

    public void Welcome()
    {
        var welcomed = new Welcomed(Id);
        Apply(welcomed);
        Raise(welcomed);
    }

    public void Apply(AccountOpened opened)
    {
        Id = opened.AccountId;
        Customer = opened.Customer;
    }

    public void Apply(Deposited deposited) => Balance += deposited.Amount;

    public void Apply(Welcomed welcomed) => IsWelcomed = true;
}
