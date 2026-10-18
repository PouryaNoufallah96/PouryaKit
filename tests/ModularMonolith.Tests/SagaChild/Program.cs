using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.Sagas;
using ModularMonolith.Tests.Sagas.Host;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine.Sagas;
using PouryaKit.Modularity;
using PouryaKit.Testing;

// The crash tests' child: pay <connection string> <tenant> <order> [in-step]. It places an order of two installments,
// waits for its saga, records one payment and waits to be killed. With in-step, the payment step prints IN STEP and waits
// there, inside its transaction, before it changes anything.
if (args is not ["pay", var connectionString, var tenant, var order, .. var rest])
{
    await Console.Error.WriteLineAsync("usage: pay <connection string> <tenant> <order> [in-step]");
    return 2;
}

if (rest is ["in-step"])
{
    SagaProbes.InPaymentStep = async () =>
    {
        Console.WriteLine("IN STEP");
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
    };
}

var builder = SagaHost.Compose(["--environment", Environments.Development, $"--ConnectionStrings:app={connectionString}"]);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.UsePouryaKitTestDelivery();
using var host = builder.Build();
await host.StartAsync();
var orders = host.Services.GetRequiredService<IGatewayProcessor<OrdersModule>>();
var context = new OperationContext(Actor.System("saga.child"), tenant);
await orders.SendAsync(new PlaceOrderCommand(Guid.Parse(order), Installments: 2, PayWithinSeconds: 3600), context);
await using (var connection = new NpgsqlConnection(connectionString))
{
    await connection.OpenAsync();
    await using var started = new NpgsqlCommand("select count(*) from wolverine.shipping_fulfilment_saga where id = $1", connection)
    {
        Parameters = { new() { Value = SagaStorage.SagaId(tenant, order) } },
    };
    while ((long)(await started.ExecuteScalarAsync())! == 0)
    {
        await Task.Delay(50);
    }
}

await orders.SendAsync(new RecordPaymentCommand(Guid.Parse(order)), context);
Console.WriteLine("PAID");
await Console.Out.FlushAsync();
await Task.Delay(Timeout.Infinite);
return 1;
