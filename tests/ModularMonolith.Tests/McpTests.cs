using System.Diagnostics;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.AskAboutProducts;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.GetProduct;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
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
// client, and disposing the Host's services ends the stdio server's process and the HTTP server's session. Concurrent
// first listings share one connection, which a listing that cancels leaves to the others; a connection that failed, or
// whose server died, is made again by a later listing; disposal ends one still under way. Against the in-process ChatStub
// and two test servers, a process this test assembly starts and one in the test process: no network.
public sealed class McpTests(AppFixture app, McpFixture mcp) : IClassFixture<McpFixture>
{
    private IChatClient Chat => mcp.Host.Services.GetRequiredService<IChatClient>();

    private IMcpServers Servers => mcp.Host.Services.GetRequiredService<IMcpServers>();

    [Fact]
    public async Task The_tools_of_a_stdio_and_an_http_server_are_listed_and_called_from_a_run()
    {
        var calls = mcp.Server.Calls.All.Count;
        var local = await Servers.ListToolsAsync(
            McpFixture.Stdio,
            TestContext.Current.CancellationToken
        );
        var remote = await Servers.ListToolsAsync(
            McpFixture.Http,
            TestContext.Current.CancellationToken
        );
        mcp.Stub.Script(
            ChatReply.Calls(
                new ToolCall("reverse", new { text = "stressed" }),
                new ToolCall("upper", new { text = "quiet" })
            ),
            ChatReply.Text("Done.")
        );

        var answer = await Chat.GetResponseAsync(
            "Reverse one, shout the other.",
            new ChatOptions { Tools = [.. local, .. remote] },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            ["process_id", "read_variable", "reverse"],
            local.Select(tool => tool.Name).Order()
        );
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
        var remote = await Servers.ListToolsAsync(
            McpFixture.Http,
            TestContext.Current.CancellationToken
        );
        mcp.Stub.Script(
            ChatReply.Calls(
                new ToolCall("erase", new { }),
                new ToolCall("upper", new { text = "loud" })
            ),
            ChatReply.Text("Done.")
        );

        await Chat.GetResponseAsync(
            "Erase it all.",
            new ChatOptions { Tools = [.. remote] },
            TestContext.Current.CancellationToken
        );

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
        var remote = await Servers.ListToolsAsync(
            McpFixture.Http,
            TestContext.Current.CancellationToken
        );
        mcp.Stub.Script([
            .. Enumerable.Repeat(
                ChatReply.Calls(new ToolCall("upper", new { text = "again" })),
                AiFixture.MaximumIterations + 1
            ),
            ChatReply.Text("Done."),
        ]);

        await Chat.GetResponseAsync(
            "Keep shouting.",
            new ChatOptions { Tools = [.. remote] },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(AiFixture.MaximumIterations, mcp.Server.Calls.All.Skip(calls).Count());
        Assert.Empty(mcp.Stub.ToolsOf(AiFixture.MaximumIterations));
    }

    [Fact]
    public async Task A_gateway_tool_and_an_mcp_tool_run_in_one_agent()
    {
        var catalog = mcp.Host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var (category, product, name) = (
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            $"Lamp {Guid.CreateVersion7():N}"
        );
        Assert.True(
            (
                await catalog.SendAsync(
                    new CreateCategoryCommand(category, $"Lamps {category:N}"),
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );
        Assert.True(
            (
                await catalog.SendAsync(
                    new CreateProductCommand(product, category, name),
                    TestContext.Current.CancellationToken
                )
            ).IsSuccess
        );
        var local = await Servers.ListToolsAsync(
            McpFixture.Stdio,
            TestContext.Current.CancellationToken
        );
        mcp.Stub.Script(
            ChatReply.Calls(
                new ToolCall("GetProduct", new { productId = product }),
                new ToolCall("reverse", new { text = "lamp" })
            ),
            ChatReply.Text("Found it.")
        );

        var answered = await mcp
            .Host.Services.GetRequiredService<IGatewayProcessor<StubModule>>()
            .QueryAsync(
                new RunAgentQuery(
                    async (run, cancellationToken) =>
                    {
                        var getProduct = catalog.Tool(
                            "GetProduct",
                            "Finds one product by its id.",
                            run,
                            (ProductToFind found) => new GetProductQuery(found.ProductId)
                        );
                        var agent = Chat.AsAIAgent(
                            "You answer questions about the shop.",
                            tools: [getProduct, .. local]
                        );
                        return (
                            await agent.RunAsync(
                                $"What is {product} called, backwards?",
                                cancellationToken: cancellationToken
                            )
                        ).Text;
                    }
                ),
                new OperationContext(Actor.Person("ada"), AppFixture.TestTenant),
                TestContext.Current.CancellationToken
            );

        Assert.True(
            answered.IsSuccess,
            string.Join(", ", answered.Errors.Select(error => error.Code))
        );
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
        var host = AppFixture.ComposeHost(
            "Development",
            app.ConnectionString,
            new RecordedLogs(),
            null,
            mcp.Arguments
        );
        int process;
        string session;
        try
        {
            var servers = host.Services.GetRequiredService<IMcpServers>();
            IReadOnlyList<McpClientTool> tools =
            [
                .. await servers.ListToolsAsync(
                    McpFixture.Stdio,
                    TestContext.Current.CancellationToken
                ),
                .. await servers.ListToolsAsync(
                    McpFixture.Http,
                    TestContext.Current.CancellationToken
                ),
            ];
            mcp.Stub.Script(
                ChatReply.Calls(
                    new ToolCall("process_id", new { }),
                    new ToolCall("upper", new { text = "bye" })
                ),
                ChatReply.Text("Done.")
            );
            await host
                .Services.GetRequiredService<IChatClient>()
                .GetResponseAsync(
                    "Which process are you?",
                    new ChatOptions { Tools = [.. tools] },
                    TestContext.Current.CancellationToken
                );
            process = int.Parse(
                string.Concat(mcp.Stub.ToolResultsOf(1)[0].Where(char.IsAsciiDigit)),
                System.Globalization.CultureInfo.InvariantCulture
            );
            session = Assert.Single(mcp.Server.Sessions.Except(sessions));

            // The run is over and owned neither client: both servers are still connected.
            Assert.True(IsRunning(process), "The stdio server's process ended with the run.");
            Assert.True(
                await mcp.Server.KeepsAsync(session, TestContext.Current.CancellationToken),
                "The HTTP server's session ended with the run."
            );
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.False(
            IsRunning(process),
            "The stdio server's process outlived the Host's services."
        );
        Assert.False(
            await mcp.Server.KeepsAsync(session, TestContext.Current.CancellationToken),
            "The HTTP server kept the session after the Host's services were disposed."
        );
    }

    [Fact]
    public async Task Concurrent_first_listings_of_a_server_share_one_process()
    {
        using var starts = new ServerStarts();
        await using var host = Compose(starts.Arguments(held: false));
        var servers = host.Services.GetRequiredService<IMcpServers>();

        var listings = await Task.WhenAll(
            Enumerable
                .Range(0, 4)
                .Select(_ =>
                    servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken)
                )
        );

        var process = Assert.Single(starts.Processes);
        Assert.All(
            await Task.WhenAll(listings.Select(ProcessIdAsync)),
            reached => Assert.Equal(process, reached)
        );
    }

    [Fact]
    public async Task A_listing_that_cancels_leaves_the_connection_under_way_to_the_listing_that_shares_it()
    {
        using var starts = new ServerStarts();
        await using var host = Compose(starts.Arguments(held: true));
        var servers = host.Services.GetRequiredService<IMcpServers>();
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var left = servers.ListToolsAsync(McpFixture.Stdio, leaving.Token);
        var staying = servers.ListToolsAsync(
            McpFixture.Stdio,
            TestContext.Current.CancellationToken
        );
        var process = await starts.StartedAsync(TestContext.Current.CancellationToken);

        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => left);
        starts.Release();

        Assert.Equal(process, await ProcessIdAsync(await staying));
        Assert.Equal([process], starts.Processes);
    }

    [Fact]
    public async Task A_listing_after_a_connection_that_failed_connects_again()
    {
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"mcp-{Guid.CreateVersion7():N}");
        try
        {
            await using var host = Compose(
                $"--PouryaKit:AI:McpServers:{McpFixture.Stdio}:WorkingDirectory={workingDirectory}"
            );
            var servers = host.Services.GetRequiredService<IMcpServers>();

            // The server's working directory does not exist yet, so its process cannot start.
            await Assert.ThrowsAnyAsync<IOException>(() =>
                servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken)
            );
            Directory.CreateDirectory(workingDirectory);

            Assert.Contains(
                "process_id",
                (
                    await servers.ListToolsAsync(
                        McpFixture.Stdio,
                        TestContext.Current.CancellationToken
                    )
                ).Select(tool => tool.Name)
            );
        }
        finally
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory);
            }
        }
    }

    [Fact]
    public async Task A_listing_after_the_stdio_servers_process_died_connects_again_to_a_new_process()
    {
        await using var host = Compose();
        var servers = host.Services.GetRequiredService<IMcpServers>();
        var died = await ProcessIdAsync(
            await servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken)
        );
        using (var process = Process.GetProcessById(died))
        {
            process.Kill();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAnyAsync<IOException>(() =>
            servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken)
        );
        var started = await ProcessIdAsync(
            await servers.ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken)
        );

        Assert.NotEqual(died, started);
        Assert.True(IsRunning(started), "The new stdio server's process is not running.");
    }

    [Fact]
    public async Task Disposing_the_hosts_services_ends_a_connection_still_under_way_without_waiting_for_it()
    {
        using var starts = new ServerStarts();
        var host = Compose(starts.Arguments(held: true));
        Task<IReadOnlyList<McpClientTool>> listing;
        int process;
        try
        {
            listing = host
                .Services.GetRequiredService<IMcpServers>()
                .ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken);
            process = await starts.StartedAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            // The held server never answers, so a disposal that waited for its connection would wait out the SDK's
            // 60 second initialization timeout.
            await host.DisposeAsync()
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.False(
            IsRunning(process),
            "The stdio server's process outlived the Host's services."
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);
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
                [
                    .. mcp.Arguments,
                    $"--PouryaKit:AI:McpServers:{McpFixture.Stdio}:Environment:MCP_GREETING=hello",
                ]
            );
            var tools = await host
                .Services.GetRequiredService<IMcpServers>()
                .ListToolsAsync(McpFixture.Stdio, TestContext.Current.CancellationToken);
            var read = tools.Single(tool => tool.Name == "read_variable");

            async Task<string> ReadAsync(string name) =>
                Assert
                    .IsType<TextContentBlock>(
                        Assert.Single(
                            (
                                await read.CallAsync(
                                    new Dictionary<string, object?> { ["name"] = name },
                                    cancellationToken: TestContext.Current.CancellationToken
                                )
                            ).Content
                        )
                    )
                    .Text;

            Assert.Equal(
                ("hello", ""),
                (await ReadAsync("MCP_GREETING"), await ReadAsync(HostSecret))
            );
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
    public async Task A_host_whose_mcp_server_options_are_invalid_fails_to_start_naming_the_option(
        string failure,
        params string[] arguments
    )
    {
        await using var host = AppFixture.ComposeHost(
            "Development",
            app.ConnectionString,
            new RecordedLogs(),
            null,
            arguments
        );

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(failure, Assert.Single(thrown.Failures));
    }

    private WebApplication Compose(params string[] arguments) =>
        AppFixture.ComposeHost(
            "Development",
            app.ConnectionString,
            new RecordedLogs(),
            null,
            [.. mcp.Arguments, .. arguments]
        );

    private static async Task<int> ProcessIdAsync(IReadOnlyList<McpClientTool> tools)
    {
        var called = await tools
            .Single(tool => tool.Name == "process_id")
            .CallAsync(cancellationToken: TestContext.Current.CancellationToken);
        return int.Parse(
            Assert.IsType<TextContentBlock>(Assert.Single(called.Content)).Text,
            System.Globalization.CultureInfo.InvariantCulture
        );
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

    // The stdio server's hooks: the directory each of its starts is recorded in, and the file a held start waits for.
    private sealed class ServerStarts : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("mcp-starts-");

        private string Hold => $"{_directory.FullName}.hold";

        public int[] Processes =>
            [
                .. _directory
                    .GetFiles()
                    .Select(file =>
                        int.Parse(file.Name, System.Globalization.CultureInfo.InvariantCulture)
                    ),
            ];

        public string[] Arguments(bool held) =>
            held
                ?
                [
                    $"--PouryaKit:AI:McpServers:{McpFixture.Stdio}:Environment:MCP_STARTS={_directory.FullName}",
                    $"--PouryaKit:AI:McpServers:{McpFixture.Stdio}:Environment:MCP_HOLD={Hold}",
                ]
                :
                [
                    $"--PouryaKit:AI:McpServers:{McpFixture.Stdio}:Environment:MCP_STARTS={_directory.FullName}",
                ];

        public async Task<int> StartedAsync(CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            while (Processes is [])
            {
                await Task.Delay(20, deadline.Token);
            }

            return Assert.Single(Processes);
        }

        public void Release() => File.WriteAllText(Hold, "");

        public void Dispose()
        {
            File.Delete(Hold);
            _directory.Delete(recursive: true);
        }
    }
}
