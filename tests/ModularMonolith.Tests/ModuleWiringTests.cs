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
    public void A_registration_error_that_only_validation_catches_fails_startup_outside_development()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<CaptiveDependencyModule>());

        var thrown = Assert.Throws<AggregateException>(() => builder.Build());

        Assert.Contains(
            $"Cannot consume scoped service '{typeof(ScopedService).FullName}' from singleton '{typeof(SingletonCapturingScoped).FullName}'",
            thrown.Message
        );
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

// A wiring mistake nothing notices until the singleton is first resolved, unless the container is validated on build.
public sealed class CaptiveDependencyModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.AddScoped<ScopedService>();
        module.AddSingleton<SingletonCapturingScoped>();
    }
}

public sealed class ScopedService;

public sealed class SingletonCapturingScoped(ScopedService scoped)
{
    public ScopedService Scoped { get; } = scoped;
}
