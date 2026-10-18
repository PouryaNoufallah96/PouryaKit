using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;

// What a Host and a Module call on the packed AI: the Host's one chat client over options it binds, a guardrail it
// registers, a Module's query as a Gateway tool a run gives the model beside an MCP server's tools, and an agent whose
// session the application's store keeps, alone or in a workflow. Compiled, never run.
var services = new ServiceCollection().AddPouryaKitAI();
services.Configure<PouryaKitAIOptions>(ai =>
{
    ai.Provider = AIProvider.Ollama;
    ai.Endpoint = new Uri("http://localhost:11434/v1");
    ai.Model = "qwen2.5:0.5b";
    ai.Tools.Add("GetOrder");
    ai.McpServers["files"] = new PouryaKitMcpServerOptions { Command = "npx", Arguments = { "-y", "@modelcontextprotocol/server-filesystem" } };
    ai.McpServers["docs"] = new PouryaKitMcpServerOptions { Endpoint = new Uri("https://docs.example.com/mcp") };
});
services.AddSingleton<IChatGuardrail, NoGuardrail>();
Func<IGatewayProcessor<OrdersModule>, OperationContext, AIFunction> getOrder = (orders, run) =>
    orders.Tool("GetOrder", "Finds one order by its id.", run, (OrderToFind order) => new GetOrderQuery(order.OrderId));
Func<IChatClient, IAgentSessionStore, IMcpServers, Task<string>> ask = async (chat, sessions, mcp) =>
{
    var agent = chat.AsAIAgent("You answer questions about orders.", tools: [.. await mcp.ListToolsAsync("files")]);
    var session = await agent.LoadSessionAsync(sessions, "conversation");
    var answer = await agent.RunAsync("Which order is late?", session);
    await agent.SaveSessionAsync(sessions, "conversation", session);
    return answer.Text + (await AgentWorkflowBuilder.BuildSequential(agent, agent).AsAIAgent().RunAsync("Check it.")).Text;
};
Console.WriteLine($"{services.Count(descriptor => descriptor.ServiceType == typeof(IChatClient))} {getOrder.Method.Name} {ask.Method.Name} {AIServiceCollectionExtensions.HttpClientName} {AIServiceCollectionExtensions.McpHttpClientName}");

sealed class OrdersModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) { }
}

sealed record OrderToFind(Guid OrderId);

sealed record GetOrderQuery(Guid OrderId) : Query<string>;

sealed class NoGuardrail : IChatGuardrail
{
    public Task<string?> CheckRequestAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<string?> CheckResponseAsync(ChatResponse response, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class NoSessions : IAgentSessionStore
{
    public Task<JsonElement?> FindAsync(string key, CancellationToken cancellationToken) => Task.FromResult<JsonElement?>(null);

    public Task SaveAsync(string key, JsonElement session, CancellationToken cancellationToken) => Task.CompletedTask;
}
