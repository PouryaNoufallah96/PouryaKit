using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using S1;

// Child process for the crash-recovery tests. The parent test kills this process with SIGKILL.
//   commit-then-hang  <connectionString> <productId> <Balanced|Solo>  commit, print COMMITTED, never flush
//   deliver-then-hang <connectionString> <productId> <Balanced|Solo>  commit and flush; the handler records its delivery and never returns
if (args.Length != 4)
{
    Console.Error.WriteLine("usage: S1.Host <commit-then-hang|deliver-then-hang> <connectionString> <productId> <Balanced|Solo>");
    return 2;
}

var (scenario, connectionString, productId) = (args[0], args[1], Guid.Parse(args[2]));
var durability = Enum.Parse<Wolverine.DurabilityMode>(args[3]);

using var host = SpikeHost.Build(new SpikeSettings(connectionString, Durability: durability));
await host.StartAsync();
Console.WriteLine($"STARTED node={host.Services.GetRequiredService<Wolverine.Runtime.IWolverineRuntime>().Options.Durability.AssignedNodeNumber}");

await using var scope = host.Services.CreateAsyncScope();
var uow = scope.ServiceProvider.GetRequiredService<SpikeUnitOfWork<CatalogDbContext>>();

switch (scenario)
{
    case "commit-then-hang":
        await uow.ExecuteAsync(async (db, outbox) =>
        {
            db.Products.Add(new Product { Id = productId, Name = "crash" });
            await outbox.PublishAsync(new ProductCreated(productId));
            return Outcome.Succeeded;
        }, new CommitHooks
        {
            AfterCommitBeforeFlush = async () =>
            {
                Console.WriteLine("COMMITTED");
                await Task.Delay(Timeout.Infinite);
            },
        });
        break;

    case "deliver-then-hang":
        HandlerGate.BlockForever = true;
        await uow.ExecuteAsync(async (db, outbox) =>
        {
            db.Products.Add(new Product { Id = productId, Name = "crash" });
            await outbox.PublishAsync(new ProductCreated(productId));
            return Outcome.Succeeded;
        });
        Console.WriteLine("FLUSHED");
        await Task.Delay(Timeout.Infinite);
        break;

    default:
        Console.Error.WriteLine($"unknown scenario {scenario}");
        return 2;
}

return 0;
