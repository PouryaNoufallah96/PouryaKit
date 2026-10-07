using Inventory.Host;
using Microsoft.Extensions.Hosting;

await InventoryHost.CreateBuilder(args).Build().RunAsync();
