using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Grpc;

/// <summary>
/// Refuses a gRPC client addressed at one of the Host's own listening URLs, at two moments. Before any hosted service
/// starts, it compares the URLs configuration decides, chosen as ASP.NET Core chooses them: the server's addresses the
/// application adds after <c>Build</c> (<c>app.Urls</c>), or else the configured <c>urls</c>, or else
/// <c>http_ports</c> and <c>https_ports</c>; each <c>Kestrel:Endpoints:{name}:Url</c> replaces those addresses unless
/// <c>preferHostingUrls</c> is set. <c>app.Urls</c> exists only on the built application, so this is a startup check
/// rather than a composition check. Kestrel keeps the listeners set in code (<c>ConfigureKestrel</c> with
/// <c>Listen</c> or <c>ListenLocalhost</c>) to itself, and chooses its default <c>http://localhost:5000</c> and a port 0
/// only as it binds, so once every hosted service has started, and only when the Host's server is Kestrel, it compares
/// the addresses Kestrel bound, which cover all three; a Host refused then had already bound them, and its start
/// fails. A Host without a server, or with another server, gets only the first comparison. An address matches when its
/// scheme and port are the listening URL's and its host is the same, or is loopback while the URL is loopback or listens
/// on every address, as Kestrel binds every host that is neither <c>localhost</c> nor an IP address. Another host on
/// the same port, such as a peer container on the 8080 every ASP.NET Core image listens on, is another Host.
/// </summary>
internal sealed class GrpcLoopbackCheck(IServiceProvider root) : IStartupCheck, IHostedLifecycleService
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services) => Refusals(ListeningUrls(services), services);

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        // Kestrel's server type is internal; its assembly is the one of its public options. Any other server, the test
        // server included, holds only the configured URLs, which the startup check compared.
        var server = root.GetService<IServer>();
        if (server?.GetType().Assembly != typeof(KestrelServerOptions).Assembly || server.Features.Get<IServerAddressesFeature>() is not { } bound)
        {
            return Task.CompletedTask;
        }

        List<string> refused = [.. Refusals(bound.Addresses, root).Select(refusal => $"{refusal} The server had already bound it, so the Host listened before its start failed.")];
        return refused.Count == 0 ? Task.CompletedTask : throw new InvalidOperationException(string.Join(Environment.NewLine, refused));
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // One line for each client addressed at one of the listening URLs.
    internal static IEnumerable<string> Refusals(IEnumerable<string> listening, IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptionsMonitor<PouryaKitGrpcClientOptions>>();
        foreach (var client in services.GetServices<GrpcClient>())
        {
            if (options.Get(client.Name).Address is { } address && listening.FirstOrDefault(url => Matches(address, url)) is { } own)
            {
                yield return $"The gRPC client {client.Name} is addressed at {address}, one of this Host's own listening URLs ({own}). A call between Modules of one Host goes through the Gateway, never a loopback; address the client at another Host.";
            }
        }
    }

    private static List<string> ListeningUrls(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var bound = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>();
        var (addresses, preferHostingUrls) = bound is { Addresses.Count: > 0 }
            ? ([.. bound.Addresses], bound.PreferHostingUrls)
            : (HostingUrls(configuration), configuration["preferHostingUrls"] is "1" || string.Equals(configuration["preferHostingUrls"], "true", StringComparison.OrdinalIgnoreCase));
        List<string> endpoints = [.. configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"]).OfType<string>()];
        return endpoints.Count == 0 || (preferHostingUrls && addresses.Count > 0) ? addresses : endpoints;
    }

    private static List<string> HostingUrls(IConfiguration configuration) =>
        configuration["urls"] is { Length: > 0 } urls
            ? Split(urls)
            : [.. from scheme in new[] { "http", "https" } from port in Split(configuration[$"{scheme}_ports"]) select $"{scheme}://*:{port}"];

    private static List<string> Split(string? value) => [.. (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static bool Matches(Uri address, string url)
    {
        BindingAddress listening;
        try
        {
            listening = BindingAddress.Parse(url);
        }
        catch (FormatException)
        {
            return false;
        }

        return string.Equals(address.Scheme, listening.Scheme, StringComparison.OrdinalIgnoreCase)
            && address.Port == listening.Port
            && (
                string.Equals(address.IdnHost, listening.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
                || (IsLoopback(address.IdnHost) && (IsEveryAddress(listening.Host) || IsLoopback(listening.Host)))
            );
    }

    private static bool IsEveryAddress(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out var ip)
            ? ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            : !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
