using Microsoft.Extensions.Hosting;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.RabbitMq;

System.Type[] types = [typeof(RabbitMqHostApplicationBuilderExtensions)];

// A Host that receives an Integration Event another Host publishes, over the packed transport, composed as a Host would.
var builder = Host.CreateApplicationBuilder(["--ConnectionStrings:broker=amqp://guest:guest@localhost:5672/"]);
builder.UsePouryaKitWolverine();
builder.Services.Receive<PriceChangedV1>("pricing.price-changed.v1");
builder.UsePouryaKitRabbitMq(connectionName: "broker", serviceName: "package-consumer");
using var host = builder.Build();
Console.WriteLine(types.Length);

internal sealed record PriceChangedV1(Guid Id, decimal Price);
