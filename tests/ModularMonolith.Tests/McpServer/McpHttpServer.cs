using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace ModularMonolith.Tests.McpServer;

/// <summary>
/// The MCP server over HTTP, in the test process on a loopback port, keeping a session for each client: its tools are
/// <see cref="HttpTools"/>, and it records each call they run and each session a client ended.
/// </summary>
public sealed class McpHttpServer : IAsyncDisposable
{
    private const string SessionHeader = "Mcp-Session-Id";

    private readonly WebApplication _server;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _sessions = [];
    private readonly List<string> _ended = [];

    private McpHttpServer(WebApplication server, McpCalls calls) => (_server, Calls) = (server, calls);

    /// <summary>The server's MCP endpoint.</summary>
    public Uri Endpoint => new($"{_server.Urls.Single()}/mcp");

    /// <summary>The tools the server ran.</summary>
    public McpCalls Calls { get; }

    /// <summary>The id of every session a client used, ended or not.</summary>
    public IReadOnlyCollection<string> Sessions
    {
        get
        {
            lock (_lock)
            {
                return [.. _sessions];
            }
        }
    }

    /// <summary>The id of every session a client ended, in order.</summary>
    public IReadOnlyList<string> Ended
    {
        get
        {
            lock (_lock)
            {
                return [.. _ended];
            }
        }
    }

    /// <summary>Starts the server on a free loopback port.</summary>
    public static async Task<McpHttpServer> StartAsync(CancellationToken cancellationToken)
    {
        var calls = new McpCalls();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(calls);
        // A server that keeps a session for each client, which the 2026-07-28 revision dropped from Streamable HTTP: the
        // client falls back to the initialize handshake, so it holds a session for its disposal to end.
        builder.Services.AddMcpServer().WithHttpTransport(http => http.SessionMode = HttpServerSessionMode.Stateful).WithTools([typeof(HttpTools)]);
        var app = builder.Build();
        var server = new McpHttpServer(app, calls);
        app.Use(server.RecordAsync);
        app.MapMcp("/mcp");
        await app.StartAsync(cancellationToken);
        return server;
    }

    private async Task RecordAsync(HttpContext http, RequestDelegate next)
    {
        if (http.Request.Headers[SessionHeader] is [{ } session])
        {
            lock (_lock)
            {
                _sessions.Add(session);
                if (HttpMethods.IsDelete(http.Request.Method))
                {
                    _ended.Add(session);
                }
            }
        }

        await next(http);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
