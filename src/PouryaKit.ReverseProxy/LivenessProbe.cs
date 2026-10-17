using Yarp.ReverseProxy.Configuration;

namespace PouryaKit.ReverseProxy;

// Turns on each cluster's active health check against the service defaults' liveness path, as far as the cluster's own
// configuration leaves either unset; YARP's other defaults stay: a 15-second interval and two consecutive failures.
internal sealed class LivenessProbe : IProxyConfigFilter
{
    private const string Path = "/alive";

    public ValueTask<ClusterConfig> ConfigureClusterAsync(ClusterConfig cluster, CancellationToken cancel)
    {
        var healthCheck = cluster.HealthCheck ?? new HealthCheckConfig();
        var active = healthCheck.Active ?? new ActiveHealthCheckConfig();
        return ValueTask.FromResult(
            cluster with
            {
                HealthCheck = healthCheck with { Active = active with { Enabled = active.Enabled ?? true, Path = active.Path ?? Path } },
            }
        );
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);
}
