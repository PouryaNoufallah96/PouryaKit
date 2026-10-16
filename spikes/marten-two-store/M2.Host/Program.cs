using M2.Host;
using M2.Ledger;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;

// The crash test's child: dotnet M2.Host.dll crash <connection string> <account id>. It opens the account, prints
// COMMITTED once Marten's transaction committed and before Wolverine's listener flushes, and waits there to be killed.
if (args is not ["crash", var connectionString, var account])
{
    Console.Error.WriteLine("usage: M2.Host crash <connection string> <account id>");
    return 2;
}

using var host = SpikeHost.Build(connectionString, new SpikeHostOptions { Marten = options => options.Listeners.Add(new StopAfterCommit()) });
await host.StartAsync();
await host
    .Services.GetRequiredService<IGatewayProcessor<LedgerModule>>()
    .SendAsync(new OpenAccountCommand(Guid.Parse(account), Guid.CreateVersion7()), new OperationContext(Actor.System("crash")));
return 1;

// A store listener runs before the one each MartenOutbox adds to its session, so this holds the flush back.
internal sealed class StopAfterCommit : DocumentSessionListenerBase
{
    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        Console.WriteLine("COMMITTED");
        await Console.Out.FlushAsync(token);
        await Task.Delay(Timeout.Infinite, token);
    }
}
