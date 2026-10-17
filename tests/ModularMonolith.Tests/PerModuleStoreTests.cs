using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.SameNamed;
using ModularMonolith.Tests.Stub;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;

namespace ModularMonolith.Tests;

// Receipts and the idempotency store selected per Module by its write model (ticket #121, ADR 0011): an EF Module keeps
// the EF ones its persistence registration and the EF messaging package key for it, and a Module of another write model
// keys stand-ins of its own, as that model's packages would, beside it in one Host. Each Host is built and never
// started, so nothing connects.
public sealed class PerModuleStoreTests
{
    public enum Order
    {
        ModulesFirst,
        MessagingFirst,
        ModulesAroundMessaging,
    }

    private static readonly OperationContext Context = new(Actor.Person("ada")) { RequestId = "r1" };

    [Theory]
    [InlineData(Order.ModulesFirst)]
    [InlineData(Order.MessagingFirst)]
    [InlineData(Order.ModulesAroundMessaging)]
    public async Task Each_module_resolves_its_own_receipts_and_idempotency_store_whatever_the_registration_order(Order order)
    {
        using var host = Compose(order);

        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.BeginOperation(Context, typeof(EfModule));
            var keys = scope.ServiceProvider.GetService<IIdempotencyStore>();
            var receipts = scope.ServiceProvider.GetService<IReceipts>();

            Assert.Same(scope.ServiceProvider.GetRequiredKeyedService<IIdempotencyStore>(typeof(EfModule)), keys);
            Assert.Equal("EfIdempotencyStore", keys!.GetType().Name);
            Assert.Same(scope.ServiceProvider.GetRequiredKeyedService<IReceipts>(typeof(EfModule)), receipts);
            Assert.Equal("EfReceipts", receipts!.GetType().Name);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.BeginOperation(Context, typeof(OtherStoreModule));

            Assert.IsType<StandInKeys>(scope.ServiceProvider.GetService<IIdempotencyStore>());
            Assert.IsType<StandInReceipts>(scope.ServiceProvider.GetService<IReceipts>());
        }

        // Through the Gateway, the dispatcher keeps the key of an idempotent command, and its success, in its Module's own
        // store.
        var sent = await host
            .Services.GetRequiredService<IGatewayProcessor<OtherStoreModule>>()
            .SendAsync(SameNamedFeatures.Command(), Context, TestContext.Current.CancellationToken);

        var (key, response) = Assert.Single(host.Services.GetRequiredService<StandInKeys.Kept>().Responses);
        Assert.Equal("r1", key.RequestId);
        Assert.Equal($"\"{sent.Value}\"", response);
    }

    [Fact]
    public async Task A_host_without_modules_keeps_its_single_receipts_and_idempotency_store()
    {
        var builder = Builder();
        builder.Services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.UsePouryaKitWolverine(connectionName: "app");
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(Context);

        Assert.Equal("EfIdempotencyStore", scope.ServiceProvider.GetRequiredService<IIdempotencyStore>().GetType().Name);
        Assert.Equal("EfReceipts", scope.ServiceProvider.GetRequiredService<IReceipts>().GetType().Name);
    }

    private static IHost Compose(Order order)
    {
        var builder = Builder();
        builder.Services.AddPouryaKitCaching();
        switch (order)
        {
            case Order.ModulesFirst:
                builder.AddPouryaKitModules(modules => modules.Add<EfModule>().Add<OtherStoreModule>());
                builder.UsePouryaKitWolverine(connectionName: "app");
                break;
            case Order.MessagingFirst:
                builder.UsePouryaKitWolverine(connectionName: "app");
                builder.AddPouryaKitModules(modules => modules.Add<EfModule>().Add<OtherStoreModule>());
                break;
            case Order.ModulesAroundMessaging:
                builder.AddPouryaKitModules(modules => modules.Add<EfModule>());
                builder.UsePouryaKitWolverine(connectionName: "app");
                builder.AddPouryaKitModules(modules => modules.Add<OtherStoreModule>());
                break;
        }

        return builder.Build();
    }

    private static HostApplicationBuilder Builder() =>
        Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Development", "--ConnectionStrings:app=Host=db;Database=app"]
        );

    private sealed class EfModule : IModuleDefinition
    {
        public void Configure(ModuleBuilder module) => module.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
    }

    // Another write model's Module: its own Unit of Work, and its own Receipts and keys, keyed by the Module.
    private sealed class OtherStoreModule : IModuleDefinition
    {
        public void Configure(ModuleBuilder module)
        {
            module.AddPouryaKitCqrs(SameNamedFeatures.Assembly);
            module.AddScoped<IUnitOfWork, StandInUnitOfWork>();
            module.AddSingleton<StandInKeys.Kept>();
            module.AddKeyedScoped<IIdempotencyStore, StandInKeys>(module.Module);
            module.AddKeyedScoped<IReceipts, StandInReceipts>(module.Module);
        }
    }

    private sealed class StandInUnitOfWork : IUnitOfWork
    {
        public Task<Result> RunAsync(Func<CancellationToken, Task<Result>> command, CancellationToken cancellationToken) =>
            command(cancellationToken);
    }

    private sealed class StandInKeys(StandInKeys.Kept kept) : IIdempotencyStore
    {
        public Task<RecordedSuccess?> ReserveAsync(IdempotencyKey key, CancellationToken cancellationToken) =>
            Task.FromResult(kept.Responses.TryGetValue(key, out var response) ? new RecordedSuccess(response) : null);

        public Task RecordAsync(IdempotencyKey key, string response, CancellationToken cancellationToken)
        {
            kept.Responses[key] = response;
            return Task.CompletedTask;
        }

        public sealed class Kept
        {
            public Dictionary<IdempotencyKey, string> Responses { get; } = [];
        }
    }

    private sealed class StandInReceipts : IReceipts
    {
        public Task<bool> RecordAsync(string subscription, Guid messageId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> RecordVersionAsync(string subscription, string aggregateId, long version, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
