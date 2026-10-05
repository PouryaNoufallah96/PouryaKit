using Catalog;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Modularity;

namespace ModularMonolith.Tests;

public sealed class ModuleWiringTests
{
    [Fact]
    public void A_module_added_twice_fails_startup_naming_it()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<EmptyModule>());

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPouryaKitModules(modules => modules.Add<EmptyModule>())
        );

        Assert.Contains($"{typeof(EmptyModule).FullName} is added twice", thrown.Message);
    }

    [Fact]
    public void The_gateway_of_a_module_never_added_cannot_be_resolved_and_the_failure_names_it()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<EmptyModule>());
        using var host = builder.Build();

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>()
        );

        Assert.Contains(typeof(CatalogModule).FullName!, thrown.Message);
    }
}

public sealed class EmptyModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) { }
}
