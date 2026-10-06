using Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModularMonolith.Host;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
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
    public void A_module_that_registers_or_removes_another_modules_handler_fails_startup_naming_both()
    {
        var registering = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        var replacing = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);

        var registered = Assert.Throws<InvalidOperationException>(() =>
            registering.AddPouryaKitModules(modules => modules.Add<StubModule>().Add<HandlerRegisteringModule>())
        );
        var replaced = Assert.Throws<InvalidOperationException>(() =>
            replacing.AddPouryaKitModules(modules => modules.Add<StubModule>().Add<HandlerReplacingModule>())
        );

        Assert.Contains(
            $"HandlerRegisteringModule registers a handler for {typeof(WriteNoteCommand).FullName}, which belongs to StubModule",
            registered.Message
        );
        Assert.Contains(
            $"HandlerReplacingModule removes the handler of {typeof(WriteNoteCommand).FullName}, which belongs to StubModule",
            replaced.Message
        );
    }

    [Fact]
    public void A_module_that_handles_a_command_without_a_unit_of_work_fails_startup_naming_it_and_a_query_only_module_needs_none()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPouryaKitModules(modules => modules.Add<UnstoredCommandModule>())
        );
        builder.AddPouryaKitModules(modules => modules.Add<QueryOnlyModule>());

        Assert.Contains(
            $"UnstoredCommandModule handles the command {typeof(WriteNoteCommand).FullName} but registers no Unit of Work",
            thrown.Message
        );
    }

    [Fact]
    public void A_registration_error_that_only_validation_catches_fails_startup_outside_development()
    {
        var host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        host.AddPouryaKitModules(modules => modules.Add<CaptiveDependencyModule>());
        // The sample's web Host sets its own defaults; AddPouryaKitModules must still win there.
        var webHost = ModularMonolithHost.CreateBuilder(["--environment", "Production"]);
        webHost.AddPouryaKitModules(modules => modules.Add<CaptiveDependencyModule>());

        var thrown = Assert.Throws<AggregateException>(() => host.Build());
        var thrownByWebHost = Assert.Throws<AggregateException>(() => ModularMonolithHost.Build(webHost));

        var captive =
            $"Cannot consume scoped service '{typeof(ScopedService).FullName}' from singleton '{typeof(SingletonCapturingScoped).FullName}'";
        Assert.Contains(captive, thrown.Message);
        Assert.Contains(captive, thrownByWebHost.Message);
    }

    [Fact]
    public void A_service_that_needs_the_gateway_of_a_module_never_added_fails_startup_naming_it()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production"]);
        builder.AddPouryaKitModules(modules => modules.Add<CatalogAskingModule>());

        var thrown = Assert.Throws<AggregateException>(() => builder.Build());

        Assert.Contains($"{typeof(IGatewayProcessor<>).FullName}[{typeof(CatalogModule).FullName}]", thrown.Message);
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

// Each takes over the Stub Module's handler for WriteNoteCommand, which would then run in the wrong Unit of Work.
public sealed class HandlerRegisteringModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.AddScoped<CommandHandler<WriteNoteCommand>, WriteNoteCommandHandler>();
}

public sealed class HandlerReplacingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.Replace(ServiceDescriptor.Scoped<CommandHandler<WriteNoteCommand>, WriteNoteCommandHandler>());
}

// Handles a command, but declares no data, so nothing would keep or discard what the command changes.
public sealed class UnstoredCommandModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.AddScoped<CommandHandler<WriteNoteCommand>, WriteNoteCommandHandler>();
}

public sealed class QueryOnlyModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) =>
        module.AddScoped<QueryHandler<ReadOperationContextQuery, OperationContext>, ReadOperationContextQueryHandler>();
}

// A Module whose service needs Catalog's Gateway, in a Host that never adds Catalog.
public sealed class CatalogAskingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module) => module.AddScoped<AsksCatalog>();
}

public sealed class AsksCatalog(IGatewayProcessor<CatalogModule> catalog)
{
    public IGatewayProcessor<CatalogModule> Catalog { get; } = catalog;
}
