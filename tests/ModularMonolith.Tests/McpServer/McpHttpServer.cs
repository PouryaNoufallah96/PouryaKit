using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace ModularMonolith.Tests.McpServer;

/// <summary>
/// The MCP server over HTTP, in the test process on a loopback port, keeping a session for each client: its tools are
/// <see cref="HttpTools"/>, and it records each call they run and each session a client used.
/// </summary>
public sealed class McpHttpServer : IAsyncDisposable
{
    private const string SessionHeader = "Mcp-Session-Id";

    private readonly WebApplication _server;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _sessions = [];

    private McpHttpServer(WebApplication server, McpCalls calls) =>
        (_server, Calls) = (server, calls);

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
        builder
            .Services.AddMcpServer()
            .WithHttpTransport(http => http.SessionMode = HttpServerSessionMode.Stateful)
            .WithTools([typeof(HttpTools)]);
        var app = builder.Build();
        var server = new McpHttpServer(app, calls);
        app.Use(server.RecordAsync);
        app.MapMcp("/mcp");
        await app.StartAsync(cancellationToken);
        return server;
    }

    /// <summary>
    /// Whether the server still keeps <paramref name="session"/>: it answers a ping in a session it keeps, and refuses one
    /// in a session it ended as not found.
    /// </summary>
    public async Task<bool> KeepsAsync(string session, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        using var ping = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":"probe","method":"ping"}""",
                Encoding.UTF8,
                "application/json"
            ),
        };
        ping.Headers.Add(SessionHeader, session);
        ping.Headers.Accept.ParseAdd("application/json");
        ping.Headers.Accept.ParseAdd("text/event-stream");
        using var answer = await client.SendAsync(ping, cancellationToken);
        return answer.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotFound => false,
            var status => throw new InvalidOperationException(
                $"The server answered a ping in session {session} with {(int)status}."
            ),
        };
    }

    private async Task RecordAsync(HttpContext http, RequestDelegate next)
    {
        if (http.Request.Headers[SessionHeader] is [{ } session])
        {
            lock (_lock)
            {
                _sessions.Add(session);
            }
        }

        await next(http);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _server.DisposeAsync();
}
