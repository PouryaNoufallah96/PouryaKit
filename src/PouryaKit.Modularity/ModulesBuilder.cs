using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Modularity;

/// <summary>The Host's list of Modules, given to <c>AddPouryaKitModules</c>.</summary>
public sealed class ModulesBuilder
{
    private readonly IServiceCollection _services;
    private readonly HostModules _modules;

    internal ModulesBuilder(IServiceCollection services)
    {
        _services = services;
        _modules = HostModules.In(services);
    }

    /// <summary>
    /// Runs <typeparamref name="TModule"/>'s Composition Root and registers its Gateway. Throws when the Module was
    /// already added to this Host, by this call or an earlier one, and when it handles a command but registers no
    /// Unit of Work.
    /// </summary>
    public ModulesBuilder Add<TModule>()
        where TModule : IModuleDefinition, new()
    {
        _modules.Add(typeof(TModule));
        new TModule().Configure(new ModuleBuilder(typeof(TModule), _services, _modules));
        _modules.EnsureUnitOfWorkForCommands(typeof(TModule), _services);
        _services.AddSingleton<IGatewayProcessor<TModule>, GatewayProcessor<TModule>>();
        return this;
    }
}
