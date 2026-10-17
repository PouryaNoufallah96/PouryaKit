using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace PouryaKit.ReverseProxy;

/// <summary>
/// Composes the Reverse proxy Capability on the application builder of a proxy Host of its own process, in front of a
/// system's Hosts.
/// </summary>
public static class ReverseProxyHostApplicationBuilderExtensions
{
    /// <summary>The configuration section the routes and clusters are read from.</summary>
    public const string SectionName = "ReverseProxy";

    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Forwards each request, by the routes and clusters of the <see cref="SectionName"/> section, to a destination
        /// of its cluster, reloaded when the configuration changes; the proxy maps them with YARP's <c>MapReverseProxy</c>
        /// and runs no Operation. A destination address that names a service, such as <c>http://catalog</c>, is resolved
        /// by service discovery into one destination per endpoint, with the endpoint providers the Host registers
        /// (<c>AddServiceDiscovery</c> of the service defaults reads them from the <c>Services</c> section). Each cluster
        /// probes the liveness path <c>/alive</c> of every destination, which a Host composing the service defaults maps,
        /// and takes a destination out of the cluster while its probe fails, so a cluster whose destinations are all out
        /// answers 503, unless the cluster's own <c>HealthCheck</c> configuration names another path, turns the probe off
        /// or names another available destinations policy. The Kit strips no header: the bearer token and the Kit's
        /// Operation Context headers reach the destination as the client sent them. YARP's standard transforms stay: it
        /// sets <c>Host</c> to the destination's, adds the <c>X-Forwarded-*</c> headers, drops the hop-by-hop and HTTP/2
        /// protocol headers, and replaces the trace context with the proxy's own span, in the client's trace.
        /// </summary>
        public IHostApplicationBuilder AddPouryaKitReverseProxy()
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder
                .Services.AddReverseProxy()
                .LoadFromConfig(builder.Configuration.GetSection(SectionName))
                .AddConfigFilter<LivenessProbe>()
                .AddServiceDiscoveryDestinationResolver();
            return builder;
        }
    }
}
