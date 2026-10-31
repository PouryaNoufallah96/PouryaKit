using Microsoft.AspNetCore.Builder;
using ModularMonolith.Tests.McpServer;
using Npgsql;
using PouryaKit.Testing;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// The <see cref="ChatStub"/>, the MCP server over HTTP, and the application started on a database of its own with its
/// chat client pointed at the stub and two MCP servers configured: <see cref="Stdio"/>, this test assembly's server started
/// as a process, and <see cref="Http"/>. A run there may be given the stdio server's tools and the HTTP server's
/// <c>upper</c>, beside the Gateway tools <see cref="AiFixture"/> allows, but not its <c>erase</c>.
/// </summary>
public sealed class McpFixture(AppFixture app) : IAsyncLifetime
{
    /// <summary>The name of the MCP server over stdio.</summary>
    public const string Stdio = "local";

    /// <summary>The name of the MCP server over HTTP.</summary>
    public const string Http = "remote";

    private ChatStub? _stub;
    private McpHttpServer? _server;
    private WebApplication? _host;

    /// <summary>The model the application's chat client calls.</summary>
    public ChatStub Stub =>
        _stub ?? throw new InvalidOperationException("The stub starts in InitializeAsync.");

    /// <summary>The MCP server over HTTP.</summary>
    public McpHttpServer Server =>
        _server ?? throw new InvalidOperationException("The server starts in InitializeAsync.");

    /// <summary>The started application.</summary>
    public WebApplication Host =>
        _host ?? throw new InvalidOperationException("The Host starts in InitializeAsync.");

    /// <summary>The configuration of the chat client and the two MCP servers, beside the sample's own.</summary>
    public string[] Arguments =>
        [
            .. AiFixture.StubArguments(Stub),
            "--PouryaKit:AI:Tools:3=process_id",
            "--PouryaKit:AI:Tools:4=reverse",
            "--PouryaKit:AI:Tools:5=upper",
            $"--PouryaKit:AI:McpServers:{Stdio}:Command=dotnet",
            $"--PouryaKit:AI:McpServers:{Stdio}:Arguments:0={typeof(StdioTools).Assembly.Location}",
            $"--PouryaKit:AI:McpServers:{Http}:Endpoint={Server.Endpoint}",
        ];

    public async ValueTask InitializeAsync()
    {
        _stub = await ChatStub.StartAsync(TestContext.Current.CancellationToken);
        _server = await McpHttpServer.StartAsync(TestContext.Current.CancellationToken);
        var database = $"mcp_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString)
        {
            Database = database,
        }.ConnectionString;
        _host = AppFixture.ComposeHost(
            "Development",
            connectionString,
            new RecordedLogs(),
            null,
            Arguments
        );
        await HostFixture.StartHostAsync(_host, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TestContext.Current.CancellationToken);
                await _host.DisposeAsync();
            }
        }
        finally
        {
            if (_server is not null)
            {
                await _server.DisposeAsync();
            }

            if (_stub is not null)
            {
                await _stub.DisposeAsync();
            }
        }
    }
}
