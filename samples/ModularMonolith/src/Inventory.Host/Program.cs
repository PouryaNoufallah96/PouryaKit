using Inventory.Host;
using JasperFx;
using Microsoft.Extensions.Hosting;

var builder = InventoryHost.CreateBuilder(args);
// Optional: logs through Serilog's console sink, and over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.AddSerilogDefaults();
return await InventoryHost.Build(builder).RunJasperFxCommands(args);
