using Inventory.Host;
using Microsoft.Extensions.Hosting;

var builder = InventoryHost.CreateBuilder(args);
// Optional: logs through Serilog's console sink, and over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.AddSerilogDefaults();
await InventoryHost.Build(builder).RunAsync();
