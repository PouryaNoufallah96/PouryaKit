using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using SagaSpike.Billing;
using SagaSpike.Kit;
using SagaSpike.Orders;
using SagaSpike.Shipping;
using Wolverine;

namespace SagaSpike.Host;

/// <summary>What a test varies in the Spike's Host.</summary>
public sealed class SpikeHostOptions
{
    /// <summary>The environment; outside Development Wolverine loads its generated code from this assembly.</summary>
    public string Environment { get; init; } = Environments.Development;

    /// <summary>The message store's schema.</summary>
    public string StoreSchema { get; init; } = "wolverine";

    /// <summary>The Kit's saga switches, each a rejected option or a probe.</summary>
    public Action<SagaSpikeOptions>? Sagas { get; init; }

    /// <summary>More Wolverine options.</summary>
    public Action<WolverineOptions>? Wolverine { get; init; }

    /// <summary>More services, registered last.</summary>
    public Action<IServiceCollection>? Services { get; init; }
}

/// <summary>The Spike's Host: Orders and Billing on EF Core, Shipping's saga, one Wolverine message store.</summary>
public static class SpikeHost
{
    /// <summary>The waits before each retry of a failed delivery, as PouryaKit.Testing's.</summary>
    public static TimeSpan[] RetryDelays { get; } = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100)];

    /// <summary>Builds the Host, not started, over <paramref name="connectionString"/>.</summary>
    public static IHost Build(string connectionString, SpikeHostOptions? options = null)
    {
        options ??= new SpikeHostOptions();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = options.Environment }
        );
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection([new("ConnectionStrings:app", connectionString)]);
        builder.AddPouryaKitModules(modules =>
        {
            modules.Add<OrdersModule>();
            modules.Add<BillingModule>();
            modules.Add<ShippingModule>();
        });
        builder.UsePouryaKitWolverine("app", options.StoreSchema, options.Wolverine);
        // PouryaKit.Testing's delivery settings, inline: the Host is also published in Release, which carries no Testing.
        builder.Services.Configure<PouryaKitMessageStoreOptions>(store => store.Durability = DurabilityMode.Solo);
        builder.Services.Configure<PouryaKitMessagingOptions>(messaging => messaging.RetryDelays = [.. RetryDelays]);
        builder.Services.ConfigureWolverine(wolverine =>
        {
            wolverine.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
            wolverine.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(100);
        });
        builder.Services.AddSingleton(_ =>
        {
            SagaSpikeOptions sagas = new();
            options.Sagas?.Invoke(sagas);
            return sagas;
        });
        options.Services?.Invoke(builder.Services);
        return builder.Build();
    }
}
