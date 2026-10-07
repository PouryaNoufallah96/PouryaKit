using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using PouryaKit.Cqrs;
using PouryaKit.Web;

System.Type[] types = [typeof(IEndpoint)];

// The packed Web's outgoing HTTP, registered and called as a Host would: the standard retry, then the Operation Context.
var services = new ServiceCollection();
var client = services.AddHttpClient("downstream");
client.AddPouryaKitResilience(HttpReplay.SafeMethods).Configure(options => options.Retry.MaxRetryAttempts = 2);
client.AddPouryaKitOperationContext();
using var provider = services.BuildServiceProvider();
using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/stock").WithOperationContext(
    new OperationContext(Actor.Person("package-consumer"))
);
var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("downstream");
Console.WriteLine(types.Length + (http is not null && request.Options.Any() ? 1 : 0));
