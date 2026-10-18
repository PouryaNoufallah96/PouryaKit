using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using SagaSpike.Contracts;
using SagaSpike.Host;
using SagaSpike.Kit;
using SagaSpike.Orders;
using SagaSpike.Shipping;
using Wolverine;
using Wolverine.Persistence.Sagas;

// The tests' child process:
//   crash-after-commit <connection string> <tenant> <order>: prints COMMITTED once a payment step committed, before the
//     step returns and its envelope is marked handled, and waits there to be killed.
//   crash-in-step <connection string> <tenant> <order> <kit|wolverine>: prints IN STEP inside a payment step, before it
//     commits, and waits there to be killed; with the Kit's claim, or with Wolverine's logical deduplication in its place.
//   published <tenant> <order>: run from a Release publish, in Production, with ConnectionStrings__app set; places an
//     order, waits for its saga, prints SAGA STARTED and exits.
// Anything else runs the JasperFx command line, codegen write among it, over ConnectionStrings__app.
switch (args)
{
    case ["crash-after-commit", var connectionString, var tenant, var order]:
        return await CrashAsync(
            connectionString,
            tenant,
            Guid.Parse(order),
            new SpikeHostOptions
            {
                Sagas = sagas => sagas.AfterCommit = async (wireName, _) =>
                {
                    if (wireName == "orders.payment-received.v1")
                    {
                        await Stop("COMMITTED");
                    }
                },
            }
        );
    case ["crash-in-step", var connectionString, var tenant, var order, var deduplication]:
        SagaProbes.InPaymentStep = () => Stop("IN STEP");
        return await CrashAsync(
            connectionString,
            tenant,
            Guid.Parse(order),
            deduplication == "wolverine" ? SpikeOptions.WolverineDeduplication : new SpikeHostOptions()
        );
    case ["published", var tenant, var order]:
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__app")!;
        using var host = SpikeHost.Build(connectionString, new SpikeHostOptions { Environment = Environments.Production });
        await host.StartAsync();
        await PlaceAsync(host, connectionString, tenant, Guid.Parse(order), host.Services.GetRequiredService<SagaSpikeOptions>());
        Console.WriteLine("SAGA STARTED");
        await host.StopAsync();
        return 0;
    }
    default:
        return await SpikeHost
            .Build(Environment.GetEnvironmentVariable("ConnectionStrings__app") ?? "Host=localhost", new SpikeHostOptions { Environment = Environments.Production })
            .RunJasperFxCommands(args);
}

static async Task<int> CrashAsync(string connectionString, string tenant, Guid order, SpikeHostOptions options)
{
    using var host = SpikeHost.Build(connectionString, options);
    await host.StartAsync();
    await PlaceAsync(host, connectionString, tenant, order, host.Services.GetRequiredService<SagaSpikeOptions>());
    await host
        .Services.GetRequiredService<IGatewayProcessor<OrdersModule>>()
        .SendAsync(new RecordPaymentCommand(order), new OperationContext(Actor.System("spike.child"), tenant));
    await Task.Delay(Timeout.Infinite);
    return 1;
}

static async Task PlaceAsync(IHost host, string connectionString, string tenant, Guid order, SagaSpikeOptions sagas)
{
    await host
        .Services.GetRequiredService<IGatewayProcessor<OrdersModule>>()
        .SendAsync(new PlaceOrderCommand(order, Installments: 2, PayWithinSeconds: 3600), new OperationContext(Actor.System("spike.child"), tenant));
    var sagaId = sagas.TenantInIdentity ? SagaStorage.SagaId(tenant, order.ToString()) : order.ToString();
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    for (var waited = 0; waited < 300; waited++)
    {
        await using var exists = new NpgsqlCommand("select count(*) from wolverine.fulfilmentsaga_saga where id = $1", connection) { Parameters = { new() { Value = sagaId } } };
        try
        {
            if ((long)(await exists.ExecuteScalarAsync())! == 1)
            {
                return;
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            // Wolverine creates the saga's table at its first step.
        }

        await Task.Delay(100);
    }

    throw new TimeoutException($"The saga {sagaId} never started.");
}

static async Task Stop(string line)
{
    Console.WriteLine(line);
    await Console.Out.FlushAsync();
    await Task.Delay(Timeout.Infinite);
}

/// <summary>Host options the tests and the child share.</summary>
public static class SpikeOptions
{
    /// <summary>
    /// The rejected option: Wolverine's own logical deduplication of each payment step, keyed as the Kit keys its claim,
    /// with the Kit's claim off.
    /// </summary>
    public static SpikeHostOptions WolverineDeduplication { get; } = new()
    {
        Sagas = sagas => sagas.ClaimKey = SagaClaimKey.None,
        Wolverine = wolverine =>
        {
            wolverine.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
            wolverine.Policies.RequireDeduplicationId(chain => chain is SagaChain { SagaType: var saga } && saga == typeof(FulfilmentSaga) && chain.MessageType == typeof(SagaInput<FulfilmentSaga, PaymentReceivedV1>));
            wolverine.MessageDeduplication.ByMessage<SagaInput<FulfilmentSaga, PaymentReceivedV1>>(paid => $"shipping.fulfilment|{paid.WireName}|{paid.EventId:D}");
        },
    };
}
