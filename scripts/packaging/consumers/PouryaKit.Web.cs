using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;
using PouryaKit.Web;

System.Type[] types = [typeof(IEndpoint)];

// The packed Web's outgoing HTTP, registered and called as a Host would: the standard retry, then the Operation Context.
var services = new ServiceCollection();
var client = services.AddHttpClient("downstream");
client.AddPouryaKitResilience(HttpReplay.SafeMethods).Configure(options => options.Retry.MaxRetryAttempts = 2);
client.AddPouryaKitOperationContext();
// The configured defaults, set as a Host binds them from its PouryaKit:Web section.
services.AddPouryaKitWeb();
services.Configure<PouryaKitCorsOptions>(cors =>
{
    var browser = new PouryaKitCorsPolicy { PreflightMaxAge = TimeSpan.FromMinutes(10) };
    browser.Origins.Add("https://app.example");
    cors.Policies["browser"] = browser;
});
services.Configure<PouryaKitCompressionOptions>(compression => compression.Enabled = true);
services.Configure<PouryaKitRateLimitOptions>(limit =>
{
    limit.Enabled = true;
    limit.PermitLimit = 10;
    limit.Window = TimeSpan.FromMinutes(1);
    limit.QueueLimit = 2;
});
using var provider = services.BuildServiceProvider();
using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/stock").WithOperationContext(
    new OperationContext(Actor.Person("package-consumer"))
);
var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient("downstream");
var queueLimit = provider.GetRequiredService<IOptions<PouryaKitRateLimitOptions>>().Value.QueueLimit;
Console.WriteLine(types.Length + (http is not null && request.Options.Any() && queueLimit == 2 ? 1 : 0));
