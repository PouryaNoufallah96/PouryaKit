using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using S2.Catalog;
using S2.Host;
using S2.Inventory;
using S2.Kit;
using S2.Kit.Harness;

// Child process for the kill test; the parent kills it with SIGKILL.
//   publish-then-hang <connectionString> <SeparatedQueues|DeliveryEnvelope> <productId>
// Publishes one ProductCreatedV1. Inventory fails every attempt and waits a minute between retries, so its envelope is
// still pending when the process dies; Billing succeeds at once.
if (args is not ["publish-then-hang", var connectionString, var choice, var id])
{
    Console.Error.WriteLine("usage: S2.Host publish-then-hang <connectionString> <SeparatedQueues|DeliveryEnvelope> <productId>");
    return 2;
}

var productId = Guid.Parse(id);
HandlerScript.Set(InventoryModule.HandlerName, productId, Plan.FailForever());

using var host = SpikeHost.Build(new SpikeSettings(connectionString, Enum.Parse<DeliveryChoice>(choice),
    RetryCooldowns: [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)]));
await host.StartAsync();

await using (var scope = host.Services.CreateAsyncScope())
{
    await CatalogModule.CreateProductAsync(scope.ServiceProvider.GetRequiredService<UnitOfWork<CatalogDbContext>>(), productId);
}

Console.WriteLine("PUBLISHED");
await Task.Delay(Timeout.Infinite);
return 0;
