using M2.Catalog;
using M2.Ledger;
using M2.Marten;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Testing;
using Wolverine;

namespace M2.Host;

/// <summary>What a test varies in the Spike's Host.</summary>
public sealed class SpikeHostOptions
{
    /// <summary>Whether the Host has the EF Module Catalog beside the Stream Module Ledger.</summary>
    public bool WithCatalog { get; init; } = true;

    /// <summary>The schema Marten's Wolverine integration keeps envelopes in; the Kit's store keeps them in <c>wolverine</c>.</summary>
    public string MessageStoreSchema { get; init; } = "wolverine";

    /// <summary>The connection string Marten's store uses; the message store uses <c>app</c>.</summary>
    public string MartenConnectionName { get; init; } = "app";

    /// <summary>Whether the Host fails when built if Marten's database is not the message store's.</summary>
    public bool CheckMartenDatabase { get; init; } = true;

    /// <summary>Whether the per-Module store resolvers are registered before the Kit's registrations rather than after.</summary>
    public bool RoutersFirst { get; init; }

    /// <summary>More configuration, such as another connection string.</summary>
    public Dictionary<string, string?> Configuration { get; init; } = [];

    /// <summary>More Marten options.</summary>
    public Action<StoreOptions>? Marten { get; init; }

    /// <summary>More Wolverine options.</summary>
    public Action<WolverineOptions>? Wolverine { get; init; }

    /// <summary>More services, registered last.</summary>
    public Action<IServiceCollection>? Services { get; init; }
}

/// <summary>The Spike's Host: Catalog on EF Core and Ledger on Marten, one Wolverine message store.</summary>
public static class SpikeHost
{
    /// <summary>Builds the Host, not started, over <paramref name="connectionString"/>.</summary>
    public static IHost Build(string connectionString, SpikeHostOptions? options = null)
    {
        options ??= new SpikeHostOptions();
        // Development: Wolverine compiles its handler code at run time there (ADR 0004).
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { EnvironmentName = Environments.Development }
        );
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection([new("ConnectionStrings:app", connectionString), .. options.Configuration]);
        if (options.RoutersFirst)
        {
            builder.Services.UseSpikeModuleStoreRouters();
        }

        builder.AddPouryaKitModules(modules =>
        {
            if (options.WithCatalog)
            {
                modules.Add<CatalogModule>();
            }

            modules.Add<LedgerModule>();
        });
        builder.AddSpikeMarten(options.MartenConnectionName, "app", options.MessageStoreSchema, options.CheckMartenDatabase, options.Marten);
        builder.UsePouryaKitWolverine("app", configure: options.Wolverine);
        builder.Services.UsePouryaKitTestDelivery();
        if (!options.RoutersFirst)
        {
            builder.Services.UseSpikeModuleStoreRouters();
        }

        options.Services?.Invoke(builder.Services);
        return builder.Build();
    }
}
