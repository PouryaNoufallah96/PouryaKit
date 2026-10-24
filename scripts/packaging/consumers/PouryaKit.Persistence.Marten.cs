using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;
using PouryaKit.Persistence.Marten;

// What a Stream Module writes on the packed package: a Stream folded by Marten's source generator, and the Module's
// registration of its Marten Unit of Work. Compiled, never run.
var services = new ServiceCollection().AddPouryaKitStreams(schema: "accounts");
Console.WriteLine(services.Count + Account.Open(Guid.CreateVersion7()).DomainEvents.Count);

internal sealed record AccountOpened(Guid AccountId) : DomainEvent;

internal sealed class Account : StreamAggregate
{
    public static Account Open(Guid id)
    {
        var account = new Account();
        var opened = new AccountOpened(id);
        account.Apply(opened);
        account.Raise(opened);
        return account;
    }

    public void Apply(AccountOpened opened) => Id = opened.AccountId;
}
