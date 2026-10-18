using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.McpServer;

// The MCP server over stdio: its tools are StdioTools, and it ends when its client closes its standard input. It writes
// nothing to standard output but the protocol's messages.
var builder = Host.CreateEmptyApplicationBuilder(settings: null);
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools([typeof(StdioTools)]);
await builder.Build().RunAsync();
