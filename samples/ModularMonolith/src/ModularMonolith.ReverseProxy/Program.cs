using Microsoft.Extensions.Hosting;
using ModularMonolith.ReverseProxy;

var builder = ReverseProxyHost.CreateBuilder(args);
// Optional: logs through Serilog's console sink, and over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.AddSerilogDefaults();
await ReverseProxyHost.Build(builder).RunAsync();
