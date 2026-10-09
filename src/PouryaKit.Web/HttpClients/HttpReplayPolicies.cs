using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Web;

/// <summary>
/// The replay policy of each standard resilience pipeline the Kit registered, by the pipeline's options name. It lives in
/// the Host's service collection, so a later <c>AddPouryaKitResilience</c> call for the same client replaces the policy:
/// a named post-configure step, once registered, cannot be removed.
/// </summary>
internal sealed class HttpReplayPolicies
{
    private readonly Dictionary<string, HttpReplay> _byPipeline = [];

    public static HttpReplayPolicies In(IServiceCollection services)
    {
        if (services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(HttpReplayPolicies)) is { } existing)
        {
            return (HttpReplayPolicies)existing.ImplementationInstance!;
        }

        var policies = new HttpReplayPolicies();
        services.AddSingleton(policies);
        return policies;
    }

    /// <summary>Records <paramref name="replay"/> as the pipeline's policy; true when the pipeline had none.</summary>
    public bool Set(string pipelineName, HttpReplay replay)
    {
        CollectionsMarshal.GetValueRefOrAddDefault(_byPipeline, pipelineName, out var existed) = replay;
        return !existed;
    }

    public HttpReplay For(string pipelineName) => _byPipeline[pipelineName];
}
