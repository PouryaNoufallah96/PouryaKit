using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace PouryaKit.ReverseProxy;

// Turns on each cluster's active health check against the service defaults' liveness path, and keeps a destination whose
// probe fails out of its cluster even when every destination is out (HealthyAndUnknown, where YARP's HealthyOrPanic
// default would forward to all of them again), as far as the cluster's own configuration leaves each unset; YARP's other
// defaults stay: a 15-second interval and two consecutive failures.
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
                HealthCheck = healthCheck with
                {
                    Active = active with { Enabled = active.Enabled ?? true, Path = active.Path ?? Path },
                    AvailableDestinationsPolicy = healthCheck.AvailableDestinationsPolicy ?? HealthCheckConstants.AvailableDestinations.HealthyAndUnknown,
                },
            }
        );
    }

    public ValueTask<RouteConfig> ConfigureRouteAsync(RouteConfig route, ClusterConfig? cluster, CancellationToken cancel) =>
        ValueTask.FromResult(route);
}
