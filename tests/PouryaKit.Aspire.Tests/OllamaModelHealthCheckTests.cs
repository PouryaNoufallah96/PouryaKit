using System.Net;
using System.Net.Sockets;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PouryaKit.Aspire.Tests;

// Aspire awaits a resource's health check with no deadline of its own, so a server that holds the request open must not
// hold the check, and the Hosts that wait for it, forever.
public sealed class OllamaModelHealthCheckTests
{
    private static readonly TimeSpan Within = TimeSpan.FromMilliseconds(200);

    // The row that goes on to pull needs /api/show answered first, and a loaded runner's first request can outlast Within.
    private static readonly TimeSpan ForAnAnswer = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(
        false,
        "Ollama did not say within 00:00:00.2000000 whether it has the model qwen2.5:0.5b."
    )]
    [InlineData(true, "Ollama did not pull the model qwen2.5:0.5b within 00:00:00.2000000.")]
    public async Task A_server_that_never_answers_leaves_the_resource_unhealthy_when_the_deadline_passes(
        bool answersShow,
        string description
    )
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var server = ServeAsync(listener, answersShow, stop.Token);
        var resource = new ContainerResource("ollama");
        var http = new EndpointAnnotation(
            ProtocolType.Tcp,
            uriScheme: "http",
            name: "http",
            targetPort: 11434
        );
        http.AllocatedEndpoint = new AllocatedEndpoint(
            http,
            "127.0.0.1",
            ((IPEndPoint)listener.LocalEndpoint).Port
        );
        resource.Annotations.Add(http);
        var check = new OllamaModelHealthCheck(
            new EndpointReference(resource, "http"),
            "qwen2.5:0.5b",
            new Clients(),
            answersShow ? ForAnAnswer : Within,
            Within
        );
        // Long past both deadlines: a check that waits for this has none of its own.
        using var patience = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        patience.CancelAfter(TimeSpan.FromSeconds(10));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), patience.Token);

        await stop.CancelAsync();
        await server;
        Assert.Equal((HealthStatus.Unhealthy, description), (result.Status, result.Description));
    }

    // Answers /api/show with 404 when told to, so the check goes on to pull; holds every other request open, unanswered.
    private static async Task ServeAsync(
        TcpListener listener,
        bool answersShow,
        CancellationToken cancellationToken
    )
    {
        List<TcpClient> held = [];
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                held.Add(client);
                _ = AnswerAsync(client.GetStream(), answersShow, cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            held.ForEach(client => client.Dispose());
        }
    }

    private static async Task AnswerAsync(
        NetworkStream stream,
        bool answersShow,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[8192];
        var request = new StringBuilder();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    return;
                }

                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                var text = request.ToString();
                if (
                    answersShow
                    && text.StartsWith("POST /api/show ", StringComparison.Ordinal)
                    && text.Contains("}", StringComparison.Ordinal)
                )
                {
                    request.Clear();
                    await stream.WriteAsync(
                        "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\n\r\n"u8.ToArray(),
                        cancellationToken
                    );
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        { }
    }

    private sealed class Clients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
