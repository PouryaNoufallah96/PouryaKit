using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PouryaKit.Aspire;

// Healthy once the server has the model, so a Host that waits for the resource starts with its model served. A model the
// volume lacks is pulled within the check, which Aspire runs again until it passes. Aspire awaits each check with no
// deadline of its own, so each request has one: a server that does not answer, a pull that fails or outlasts its deadline,
// or one that dies with the container leaves the resource unhealthy, and the next check asks again.
internal sealed class OllamaModelHealthCheck(
    EndpointReference endpoint,
    string model,
    IHttpClientFactory clients,
    TimeSpan showWithin,
    TimeSpan pullWithin
) : IHealthCheck
{
    /// <summary>How long the server has to say whether it has the model.</summary>
    public static readonly TimeSpan ShowWithin = TimeSpan.FromSeconds(10);

    /// <summary>How long a pull, the whole model's download, may take.</summary>
    public static readonly TimeSpan PullWithin = TimeSpan.FromMinutes(30);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken
    )
    {
        using var client = clients.CreateClient();
        // Each request's own deadline bounds it instead.
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.BaseAddress = new Uri(endpoint.Url);
        using var shown = await PostWithinAsync(
            client,
            "/api/show",
            new { model },
            showWithin,
            cancellationToken
        );
        if (shown is null)
        {
            return HealthCheckResult.Unhealthy(
                $"Ollama did not say within {showWithin} whether it has the model {model}."
            );
        }

        if (shown.IsSuccessStatusCode)
        {
            return HealthCheckResult.Healthy();
        }

        if (shown.StatusCode != HttpStatusCode.NotFound)
        {
            return HealthCheckResult.Unhealthy(
                $"Ollama answered {(int)shown.StatusCode} for the model {model}."
            );
        }

        using var pulled = await PostWithinAsync(
            client,
            "/api/pull",
            new { model, stream = false },
            pullWithin,
            cancellationToken
        );
        return pulled switch
        {
            null => HealthCheckResult.Unhealthy(
                $"Ollama did not pull the model {model} within {pullWithin}."
            ),
            { IsSuccessStatusCode: true } => HealthCheckResult.Healthy(),
            _ => HealthCheckResult.Unhealthy(
                $"Ollama could not pull the model {model}: {await pulled.Content.ReadAsStringAsync(cancellationToken)}"
            ),
        };
    }

    // The response, read whole, or null when the deadline passed first; the caller's own cancellation still throws.
    private static async Task<HttpResponseMessage?> PostWithinAsync(
        HttpClient client,
        string path,
        object body,
        TimeSpan within,
        CancellationToken cancellationToken
    )
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(within);
        try
        {
            return await client.PostAsJsonAsync(path, body, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
