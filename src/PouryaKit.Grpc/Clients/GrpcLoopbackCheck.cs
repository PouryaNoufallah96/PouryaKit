using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

namespace PouryaKit.Grpc;

/// <summary>
/// Refuses a gRPC client addressed at one of the Host's own listening URLs: the configured <c>urls</c>,
/// <c>http_ports</c> and <c>https_ports</c>, each <c>Kestrel:Endpoints:{name}:Url</c>, and the server's addresses the
/// application adds after <c>Build</c> (<c>app.Urls</c>). Those last exist only on the built application, so this is a
/// startup check rather than a composition check; it runs before the server binds, so an address the server chooses
/// only as it binds, such as port 0 or a <c>Listen</c> call in code, is not seen, and the Kit's skill states the rule
/// for it. An address matches when its scheme and port are the listening URL's and its host is the same, or is loopback
/// while the URL is loopback or listens on every address. Another host on the same port, such as a peer container on
/// the 8080 every ASP.NET Core image listens on, is another Host.
/// </summary>
internal sealed class GrpcLoopbackCheck : IStartupCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var listening = ListeningUrls(services).ToList();
        var options = services.GetRequiredService<IOptionsMonitor<PouryaKitGrpcClientOptions>>();
        foreach (var client in services.GetServices<GrpcClient>())
        {
            if (options.Get(client.Name).Address is { } address && listening.FirstOrDefault(url => Matches(address, url)) is { } own)
            {
                yield return $"The gRPC client {client.Name} is addressed at {address}, one of this Host's own listening URLs ({own}). A call between Modules of one Host goes through the Gateway, never a loopback; address the client at another Host.";
            }
        }
    }

    private static IEnumerable<string> ListeningUrls(IServiceProvider services)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var urls = (configuration["urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ports =
            from scheme in new[] { "http", "https" }
            from port in (configuration[$"{scheme}_ports"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            select $"{scheme}://*:{port}";
        var endpoints = configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"]).OfType<string>();
        var server = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        return urls.Concat(ports).Concat(endpoints).Concat(server).Distinct(StringComparer.OrdinalIgnoreCase);
    }

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
        host is "*" or "+" || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)));

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
