using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using PouryaKit.ReverseProxy;

namespace ModularMonolith.ReverseProxy;

/// <summary>
/// The Reverse proxy in front of the Proving Sample's Hosts, in a process of its own: routes and clusters from the
/// <c>ReverseProxy</c> section, destinations resolved from the <c>Services</c> section. It runs no Operation. Its
/// settings turn off the probe of <c>ModularMonolith.Host</c>, which composes no service defaults and so maps no
/// liveness path. The composition is shared by <c>Program</c> and the tests that host it.
/// </summary>
public static class ReverseProxyHost
{
    /// <summary>The proxy's composition, from the configuration <paramref name="args"/> adds to.</summary>
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // OpenTelemetry, whose ASP.NET Core and HttpClient spans are the proxy's in the client's trace, health checks,
        // service discovery and the standard HTTP resilience: the recipes of ModularMonolith.ServiceDefaults.
        builder.AddServiceDefaults();
        builder.AddPouryaKitReverseProxy();
        return builder;
    }

    /// <summary>Builds the proxy <paramref name="builder"/> composed, with its health endpoints and its routes.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.MapDefaultEndpoints();
        app.MapReverseProxy();
        return app;
    }
}
