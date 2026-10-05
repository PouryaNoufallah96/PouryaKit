using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PouryaKit.Modularity;

/// <summary>The Host's list of Modules, given to <c>AddPouryaKitModules</c>.</summary>
public sealed class ModulesBuilder
{
    private readonly IServiceCollection _services;

    internal ModulesBuilder(IServiceCollection services) => _services = services;

    /// <summary>Runs <typeparamref name="TModule"/>'s Composition Root and registers its Gateway.</summary>
    public ModulesBuilder Add<TModule>()
        where TModule : IModuleDefinition, new()
    {
        new TModule().Configure(new ModuleBuilder(typeof(TModule), _services));
        _services.TryAddSingleton<IGatewayProcessor<TModule>, GatewayProcessor<TModule>>();
        return this;
    }
}
