using PouryaKit.ReverseProxy;

// The Reverse proxy in front of the Host, in a process of its own. Routes and clusters come from the ReverseProxy section,
// the Host's endpoints from the Services section, and each cluster probes its Hosts' /alive. It runs no Operation and
// strips no header: the bearer token reaches the Host as the client sent it, so the Host authenticates the caller itself.
// YARP's standard transforms still set Host, add X-Forwarded-* and replace the trace context.
var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry, whose spans are the proxy's in the caller's trace, health checks and service discovery, from the
// application's ServiceDefaults project; then Serilog in place of the logging providers.
builder.AddServiceDefaults();
builder.AddSerilogDefaults();
builder.AddPouryaKitReverseProxy();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapReverseProxy();
await app.RunAsync();
