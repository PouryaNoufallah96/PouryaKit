using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.McpServer;

// The MCP server over stdio: its tools are StdioTools, and it ends when its client closes its standard input. It writes
// nothing to standard output but the protocol's messages. Two hooks a test sets through the server's Environment option:
// each start writes a file named by its process id into the directory MCP_STARTS names, and with MCP_HOLD set a start
// answers nothing until the file it names exists, so a test sees a connection while it is still under way.
if (Environment.GetEnvironmentVariable("MCP_STARTS") is { Length: > 0 } starts)
{
    await File.WriteAllTextAsync(Path.Combine(starts, $"{Environment.ProcessId}"), "");
}

if (Environment.GetEnvironmentVariable("MCP_HOLD") is { Length: > 0 } hold)
{
    while (!File.Exists(hold))
    {
        await Task.Delay(20);
    }
}

var builder = Host.CreateEmptyApplicationBuilder(settings: null);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools([typeof(StdioTools)]);
await builder.Build().RunAsync();
