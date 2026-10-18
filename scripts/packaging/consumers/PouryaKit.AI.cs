using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.AI;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;

// What a Host and a Module call on the packed AI: the Host's one chat client over options it binds, and a Module's query
// as a Gateway tool a run gives the model. Compiled, never run.
var services = new ServiceCollection().AddPouryaKitAI();
services.Configure<PouryaKitAIOptions>(ai =>
{
    ai.Provider = AIProvider.Ollama;
    ai.Endpoint = new Uri("http://localhost:11434/v1");
    ai.Model = "qwen2.5:0.5b";
    ai.Tools.Add("GetOrder");
});
Func<IGatewayProcessor<OrdersModule>, OperationContext, AIFunction> getOrder = (orders, run) =>
    orders.Tool("GetOrder", "Finds one order by its id.", run, (OrderToFind order) => new GetOrderQuery(order.OrderId));
Console.WriteLine($"{services.Count(descriptor => descriptor.ServiceType == typeof(IChatClient))} {getOrder.Method.Name} {AIServiceCollectionExtensions.HttpClientName}");

sealed class OrdersModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) { }
}

sealed record OrderToFind(Guid OrderId);

sealed record GetOrderQuery(Guid OrderId) : Query<string>;
