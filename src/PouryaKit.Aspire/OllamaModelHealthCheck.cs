using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PouryaKit.Aspire;

// Healthy once the server has the model, so a Host that waits for the resource starts with its model served. A model the
// volume lacks is pulled within the check, which Aspire runs again until it passes: an unreachable server or a pull that
// fails, is cancelled or dies with the container leaves the resource unhealthy, and the next check asks again.
internal sealed class OllamaModelHealthCheck(EndpointReference endpoint, string model, IHttpClientFactory clients) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient();
        // A first pull downloads the whole model in this one request.
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.BaseAddress = new Uri(endpoint.Url);
        using var shown = await client.PostAsJsonAsync("/api/show", new { model }, cancellationToken);
        if (shown.IsSuccessStatusCode)
        {
            return HealthCheckResult.Healthy();
        }

        if (shown.StatusCode != HttpStatusCode.NotFound)
        {
            return HealthCheckResult.Unhealthy($"Ollama answered {(int)shown.StatusCode} for the model {model}.");
        }

        using var pulled = await client.PostAsJsonAsync("/api/pull", new { model, stream = false }, cancellationToken);
        return pulled.IsSuccessStatusCode
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy($"Ollama could not pull the model {model}: {await pulled.Content.ReadAsStringAsync(cancellationToken)}");
    }
}
