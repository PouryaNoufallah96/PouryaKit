using Microsoft.Extensions.Hosting;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.RabbitMq;

System.Type[] types = [typeof(RabbitMqHostApplicationBuilderExtensions)];

// What a Host that receives an Integration Event another Host publishes calls on the packed transport. It is compiled,
// never run: a real Host also subscribes to what it receives, and without that this one's Build would fail its checks.
var builder = Host.CreateApplicationBuilder([
    "--ConnectionStrings:broker=amqp://guest:guest@localhost:5672/",
]);
builder.UsePouryaKitWolverine();
builder.Services.Receive<PriceChangedV1>("pricing.price-changed.v1");
builder.UsePouryaKitRabbitMq(connectionName: "broker", serviceName: "package-consumer");
using var host = builder.Build();
Console.WriteLine(types.Length);

internal sealed record PriceChangedV1(Guid Id, decimal Price);
