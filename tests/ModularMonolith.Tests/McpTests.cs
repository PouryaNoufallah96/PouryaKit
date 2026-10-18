using System.Diagnostics;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.AskAboutProducts;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModularMonolith.Tests.Stub;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;

namespace ModularMonolith.Tests;

// Ticket #137: the Host's clients of the MCP servers its options name, over stdio and over HTTP, owned by its services:
// their tools join a run beside the Gateway tools, under the one allow-list and the one iteration cap of the Host's chat
// client, and disposing the Host's services ends the stdio server's process and the HTTP server's session. Against the
// in-process ChatStub and two test servers, a process this test assembly starts and one in the test process: no network.
public sealed class McpTests(AppFixture app, McpFixture mcp) : IClassFixture<McpFixture>
{
    private IChatClient Chat => mcp.Host.Services.GetRequiredService<IChatClient>();

    private IMcpServers Servers => mcp.Host.Services.GetRequiredService<IMcpServers>();

    [Fact]
    public async Task The_tools_of_a_stdio_and_an_http_server_are_listed_and_called_from_a_run()
    {
        var calls = mcp.Server.Calls.All.Count;
        var local = await Servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken);
        var remote = await Servers.ListToolsAsync(McpFixture.Http, TestContext.Current.CancellationToken);
        mcp.Stub.Script(ChatReply.Calls(new ToolCall("reverse", new { text = "stressed" }), new ToolCall("upper", new { text = "quiet" })), ChatReply.Text("Done."));

        var answer = await Chat.GetResponseAsync("Reverse one, shout the other.", new ChatOptions { Tools = [.. local, .. remote] }, TestContext.Current.CancellationToken);

        Assert.Equal(["process_id", "read_variable", "reverse"], local.Select(tool => tool.Name).Order());
        Assert.Equal(["erase", "upper"], remote.Select(tool => tool.Name).Order());
        Assert.Equal("Done.", answer.Text);
        var results = mcp.Stub.ToolResultsOf(1);
        Assert.Equal(2, results.Count);
        Assert.Contains("desserts", results[0], StringComparison.Ordinal);
        Assert.Contains("QUIET", results[1], StringComparison.Ordinal);
        Assert.Equal(["upper"], mcp.Server.Calls.All.Skip(calls));
    }

    [Fact]
    public async Task The_allow_list_withholds_an_mcp_tool_it_does_not_name_and_a_call_of_it_never_reaches_the_server()
    {
        var calls = mcp.Server.Calls.All.Count;
        var remote = await Servers.ListToolsAsync(McpFixture.Http, TestContext.Current.CancellationToken);
        mcp.Stub.Script(ChatReply.Calls(new ToolCall("erase", new { }), new ToolCall("upper", new { text = "loud" })), ChatReply.Text("Done."));

        await Chat.GetResponseAsync("Erase it all.", new ChatOptions { Tools = [.. remote] }, TestContext.Current.CancellationToken);

        Assert.Equal(["upper"], mcp.Stub.ToolsOf(0));
        var results = mcp.Stub.ToolResultsOf(1);
        Assert.Equal("Error: Requested function \"erase\" not found.", results[0]);
        Assert.Contains("LOUD", results[1], StringComparison.Ordinal);
        Assert.Equal(["upper"], mcp.Server.Calls.All.Skip(calls));
    }

    [Fact]
    public async Task The_iteration_cap_counts_mcp_tool_calls()
    {
        var calls = mcp.Server.Calls.All.Count;
        var remote = await Servers.ListToolsAsync(McpFixture.Http, TestContext.Current.CancellationToken);
        mcp.Stub.Script([.. Enumerable.Repeat(ChatReply.Calls(new ToolCall("upper", new { text = "again" })), AiFixture.MaximumIterations + 1), ChatReply.Text("Done.")]);

        await Chat.GetResponseAsync("Keep shouting.", new ChatOptions { Tools = [.. remote] }, TestContext.Current.CancellationToken);

        Assert.Equal(AiFixture.MaximumIterations, mcp.Server.Calls.All.Skip(calls).Count());
        Assert.Empty(mcp.Stub.ToolsOf(AiFixture.MaximumIterations));
    }

    [Fact]
    public async Task A_gateway_tool_and_an_mcp_tool_run_in_one_agent()
    {
        var catalog = mcp.Host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var (category, product, name) = (Guid.CreateVersion7(), Guid.CreateVersion7(), $"Lamp {Guid.CreateVersion7():N}");
        Assert.True((await catalog.SendAsync(new CreateCategoryCommand(category, $"Lamps {category:N}"), TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await catalog.SendAsync(new CreateProductCommand(product, category, name), TestContext.Current.CancellationToken)).IsSuccess);
        var local = await Servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken);
        mcp.Stub.Script(
            ChatReply.Calls(new ToolCall("GetProduct", new { productId = product }), new ToolCall("reverse", new { text = "lamp" })),
            ChatReply.Text("Found it.")
        );

        var answered = await mcp.Host.Services.GetRequiredService<IGatewayProcessor<StubModule>>()
            .QueryAsync(
                new RunAgentQuery(async (run, cancellationToken) =>
                {
                    var getProduct = catalog.Tool("GetProduct", "Finds one product by its id.", run, (ProductToFind found) => new GetProductQuery(found.ProductId));
                    var agent = Chat.AsAIAgent("You answer questions about the shop.", tools: [getProduct, .. local]);
                    return (await agent.RunAsync($"What is {product} called, backwards?", cancellationToken: cancellationToken)).Text;
                }),
                new OperationContext(Actor.Person("ada"), AppFixture.TestTenant),
                TestContext.Current.CancellationToken
            );

        Assert.True(answered.IsSuccess, string.Join(", ", answered.Errors.Select(error => error.Code)));
        Assert.Equal("Found it.", answered.Value);
        Assert.Equal(["GetProduct", "process_id", "reverse"], mcp.Stub.ToolsOf(0).Order());
        var results = mcp.Stub.ToolResultsOf(1);
        Assert.Contains(name, results[0], StringComparison.Ordinal);
        Assert.Contains("pmal", results[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposing_the_hosts_services_ends_the_stdio_servers_process_and_the_http_servers_session_that_runs_left_open()
    {
        var sessions = mcp.Server.Sessions;
        var host = AppFixture.ComposeHost("Development", app.ConnectionString, new RecordedLogs(), null, mcp.Arguments);
        int process;
        string session;
        try
        {
            var servers = host.Services.GetRequiredService<IMcpServers>();
            IReadOnlyList<McpClientTool> tools =
            [
                .. await servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken),
                .. await servers.ListToolsAsync(McpFixture.Http, TestContext.Current.CancellationToken),
            ];
            mcp.Stub.Script(ChatReply.Calls(new ToolCall("process_id", new { }), new ToolCall("upper", new { text = "bye" })), ChatReply.Text("Done."));
            await host.Services.GetRequiredService<IChatClient>()
                .GetResponseAsync("Which process are you?", new ChatOptions { Tools = [.. tools] }, TestContext.Current.CancellationToken);
            process = int.Parse(string.Concat(mcp.Stub.ToolResultsOf(1)[0].Where(char.IsAsciiDigit)), System.Globalization.CultureInfo.InvariantCulture);
            session = Assert.Single(mcp.Server.Sessions.Except(sessions));

            // The run is over and owned neither client: both servers are still connected.
            Assert.True(IsRunning(process), "The stdio server's process ended with the run.");
            Assert.DoesNotContain(session, mcp.Server.Ended);
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.False(IsRunning(process), "The stdio server's process outlived the Host's services.");
        Assert.Contains(session, mcp.Server.Ended);
    }

    [Fact]
    public async Task A_stdio_server_inherits_none_of_the_hosts_environment_but_the_variables_its_options_name()
    {
        const string HostSecret = "POURYAKIT_MCP_TEST_SECRET";
        System.Environment.SetEnvironmentVariable(HostSecret, "leaked");
        try
        {
            await using var host = AppFixture.ComposeHost(
                "Development",
                app.ConnectionString,
                new RecordedLogs(),
                null,
                [.. mcp.Arguments, $"--PouryaKit:AI:McpServers:{McpFixture.Stdio}:Environment:MCP_GREETING=hello"]
            );
            var tools = await host.Services.GetRequiredService<IMcpServers>().ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken);
            var read = tools.Single(tool => tool.Name == "read_variable");

            async Task<string> ReadAsync(string name) =>
                Assert.IsType<TextContentBlock>(
                        Assert.Single((await read.CallAsync(new Dictionary<string, object?> { ["name"] = name }, cancellationToken: TestContext.Current.CancellationToken)).Content)
                    )
                    .Text;

            Assert.Equal(("hello", ""), (await ReadAsync("MCP_GREETING"), await ReadAsync(HostSecret)));
            Assert.NotEqual("", await ReadAsync("PATH"));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(HostSecret, null);
        }
    }

    [Theory]
    [InlineData(
        "PouryaKitAIOptions.McpServers names a server with neither or both of Command and Endpoint; give each a Command to start it over stdio or an Endpoint to reach it over HTTP.",
        "--PouryaKit:AI:McpServers:files:Command=dotnet",
        "--PouryaKit:AI:McpServers:files:Endpoint=http://127.0.0.1:1/mcp"
    )]
    [InlineData(
        "PouryaKitAIOptions.McpServers names a server with neither or both of Command and Endpoint; give each a Command to start it over stdio or an Endpoint to reach it over HTTP.",
        "--PouryaKit:AI:McpServers:files:WorkingDirectory=/tmp"
    )]
    [InlineData(
        "PouryaKitAIOptions.McpServers names a server whose Endpoint is not an absolute HTTP or HTTPS address.",
        "--PouryaKit:AI:McpServers:files:Endpoint=ftp://127.0.0.1/mcp"
    )]
    public async Task A_host_whose_mcp_server_options_are_invalid_fails_to_start_naming_the_option(string failure, params string[] arguments)
    {
        await using var host = AppFixture.ComposeHost("Development", app.ConnectionString, new RecordedLogs(), null, arguments);

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(failure, Assert.Single(thrown.Failures));
    }

    private static bool IsRunning(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
