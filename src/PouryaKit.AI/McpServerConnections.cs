using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace PouryaKit.AI;

// The Host's clients of its MCP servers, a singleton the container creates and disposes: no run owns a client, so none
// ends one. Concurrent first listings of a server share its one connection, which runs under the Host's lifetime alone: a
// caller that cancels stops waiting for it and leaves it connecting for the others, and a connection that failed is tried
// again by the next listing. Disposal ends every client, a connection still under way included: a stdio server's process
// is told to exit, then killed, and an HTTP server's session is ended with a DELETE.
internal sealed class McpServerConnections(IOptions<PouryaKitAIOptions> options, IHttpClientFactory http, ILoggerFactory loggers)
    : IMcpServers,
        IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task<McpClient>> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _disposing = new();
    private bool _disposed;

    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(string server, CancellationToken cancellationToken = default)
    {
        var client = await Connect(server).WaitAsync(cancellationToken);
        return [.. await client.ListToolsAsync(cancellationToken: cancellationToken)];
    }

    public async ValueTask DisposeAsync()
    {
        Task<McpClient>[] clients;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            clients = [.. _clients.Values];
        }

        await _disposing.CancelAsync();
        await Task.WhenAll(clients.Select(EndAsync));
        _disposing.Dispose();
    }

    private Task<McpClient> Connect(string server)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_clients.TryGetValue(server, out var connecting) && !connecting.IsFaulted && !connecting.IsCanceled)
            {
                return connecting;
            }

            if (!options.Value.McpServers.TryGetValue(server, out var configured))
            {
                throw new InvalidOperationException(
                    $"PouryaKitAIOptions.McpServers names no server '{server}'; it names {(options.Value.McpServers.Count == 0 ? "none" : string.Join(", ", options.Value.McpServers.Keys))}."
                );
            }

            return _clients[server] = McpClient.CreateAsync(Transport(server, configured), loggerFactory: loggers, cancellationToken: _disposing.Token);
        }
    }

    // The HTTP transport opens no standing GET stream for messages the server sends unasked: its tools answer each call on
    // the call's own response, and a standing request would be cut by the attempt timeout of a Host's resilience pipeline.
    private IClientTransport Transport(string name, PouryaKitMcpServerOptions server) =>
        server.Endpoint is { } endpoint
            ? new HttpClientTransport(
                new()
                {
                    Name = name,
                    Endpoint = endpoint,
                    EnableStandaloneGetStream = false,
                },
                http.CreateClient(AIServiceCollectionExtensions.McpHttpClientName),
                loggers,
                ownsHttpClient: true
            )
            : new StdioClientTransport(
                new()
                {
                    Name = name,
                    Command = server.Command!,
                    Arguments = [.. server.Arguments],
                    WorkingDirectory = server.WorkingDirectory,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = Environment(server),
                },
                loggers
            );

    private static Dictionary<string, string?> Environment(PouryaKitMcpServerOptions server)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var (key, value) in server.Environment)
        {
            environment[key] = value;
        }

        return environment;
    }

    private static async Task EndAsync(Task<McpClient> connecting)
    {
        McpClient client;
        try
        {
            client = await connecting;
        }
        catch
        {
            // A connection that failed or was cancelled holds no client to end.
            return;
        }

        await client.DisposeAsync();
    }
}
