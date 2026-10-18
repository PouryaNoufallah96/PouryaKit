using Marten;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.StreamHost;
using ModularMonolith.Tests.Streams;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using IChangeSet = Marten.Services.IChangeSet;

// The crash test's child: crash <connection string> <account id>. It opens the account, prints COMMITTED once Marten's
// transaction committed and before Wolverine's listener sends what it committed, and waits there to be killed.
if (args is not ["crash", var connectionString, var account])
{
    await Console.Error.WriteLineAsync("usage: crash <connection string> <account id>");
    return 2;
}

var builder = StreamHosts.MartenOnly(connectionString);
builder.Services.ConfigureMarten(options => options.Listeners.Add(new StopAfterCommit()));
using var host = builder.Build();
await host.StartAsync();
await host
    .Services.GetRequiredService<IGatewayProcessor<AccountsModule>>()
    .SendAsync(new OpenAccountCommand(Guid.Parse(account), Guid.CreateVersion7()), new OperationContext(Actor.System("crash")));
return 1;

// A store listener runs after Marten's commit, before every listener of the session, Wolverine's send included.
internal sealed class StopAfterCommit : DocumentSessionListenerBase
{
    public override async Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        Console.WriteLine("COMMITTED");
        await Console.Out.FlushAsync(token);
        await Task.Delay(Timeout.Infinite, token);
    }
}
