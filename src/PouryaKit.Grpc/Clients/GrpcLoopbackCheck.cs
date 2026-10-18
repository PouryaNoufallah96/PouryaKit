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
/// Refuses a gRPC client addressed at one of the Host's own listening URLs, chosen as ASP.NET Core chooses them: the
/// server's addresses the application adds after <c>Build</c> (<c>app.Urls</c>), or else the configured <c>urls</c>, or
/// else <c>http_ports</c> and <c>https_ports</c>; each <c>Kestrel:Endpoints:{name}:Url</c> replaces those addresses
/// unless <c>preferHostingUrls</c> is set; with none of them, Kestrel's default <c>http://localhost:5000</c>, the one
/// address it binds when nothing else is configured. <c>app.Urls</c> exists only on the built application, so this is a
/// startup check rather than a composition check; it runs before the server binds, so an address the server chooses only
/// as it binds, such as port 0 or a <c>Listen</c> call in code, is not seen, and the Kit's skill states the rule for it. An
/// address matches when its scheme and port are the listening URL's and its host is the same, or is loopback while the
/// URL is loopback or listens on every address, as Kestrel binds every host that is neither <c>localhost</c> nor an IP
/// address. Another host on the same port, such as a peer container on the 8080 every ASP.NET Core image listens on, is
/// another Host.
/// </summary>
internal sealed class GrpcLoopbackCheck : IStartupCheck
{
    // What Kestrel binds when it is given no address and no endpoint (its DefaultAddressStrategy); no HTTPS default.
    private const string DefaultUrl = "http://localhost:5000";

    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var listening = ListeningUrls(services);
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
        var server = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>();
        var (addresses, preferHostingUrls) = server is { Addresses.Count: > 0 }
            ? ([.. server.Addresses], server.PreferHostingUrls)
            : (HostingUrls(configuration), configuration["preferHostingUrls"] is "1" || string.Equals(configuration["preferHostingUrls"], "true", StringComparison.OrdinalIgnoreCase));
        List<string> endpoints = [.. configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(endpoint => endpoint["Url"]).OfType<string>()];
        var listening = endpoints.Count == 0 || (preferHostingUrls && addresses.Count > 0) ? addresses : endpoints;
        return listening.Count > 0 ? listening : [DefaultUrl];
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
