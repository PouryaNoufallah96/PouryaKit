using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

/// <summary>
/// A Host's final composition, as the Kit's container was built from it: every registration, the Modules it composed and
/// the Feature assemblies the Kit's registrations scanned for each of them and for the application's own services, named
/// or inferred (<see cref="IModuleServices"/>), and which of them are tenant-scoped. The container registers it when <c>AddPouryaKitCompositionChecks</c>
/// installed it, and every <see cref="ICompositionCheck"/> reads it when the container is built. Every Module that
/// <c>AddPouryaKitModules</c> added is among <see cref="Modules"/>.
/// </summary>
public sealed class HostComposition
{
    private readonly Dictionary<Type, IReadOnlyList<Assembly>> _moduleAssemblies = [];
    private readonly IReadOnlyList<Assembly> _application = [];
    private readonly HashSet<Type?> _tenantScoped = [];
    private readonly List<(Type? Module, Assembly Assembly)> _dispatching = [];

    internal HostComposition(IEnumerable<ServiceDescriptor> registrations)
    {
        Registrations = [.. registrations];
        var features = Registrations
            .Select(descriptor =>
                descriptor.IsKeyedService ? null : descriptor.ImplementationInstance
            )
            .OfType<ComposedFeatures>()
            .FirstOrDefault();
        if (features is null)
        {
            Modules = [];
            return;
        }

        Modules = [.. features.Modules];
        foreach (var (module, assemblies) in features.ModuleAssemblies)
        {
            _moduleAssemblies.Add(module, [.. assemblies]);
        }

        _application = [.. features.Application];
        _tenantScoped = [.. features.TenantScoped];
        _dispatching = [.. features.Dispatching];
    }

    /// <summary>Every registration of the Host, in the order the container was built from them.</summary>
    public IReadOnlyList<ServiceDescriptor> Registrations { get; }

    /// <summary>The definition type of every Module the Host composed, in the order <c>AddPouryaKitModules</c> added it.</summary>
    public IReadOnlyList<Type> Modules { get; }

    /// <summary>
    /// The Feature assemblies the Kit's registrations scanned for <paramref name="module"/>, on its builder or for a
    /// declaration the Host's composition named it in, or for the application's own services when it is null, in the
    /// order first scanned; none when no registration scanned any.
    /// </summary>
    public IReadOnlyList<Assembly> FeatureAssembliesOf(Type? module) =>
        module is null ? _application : _moduleAssemblies.GetValueOrDefault(module) ?? [];

    /// <summary>
    /// True when <paramref name="module"/>, or the application's own services when it is null, is tenant-scoped: its
    /// Composition Root, or the application, called <see cref="TenancyServiceCollectionExtensions.AddPouryaKitTenancy"/>.
    /// For a Kit package's registration or check that holds such a Module to its tenant.
    /// </summary>
    public bool IsTenantScoped(Type? module) => _tenantScoped.Contains(module);

    // The assemblies AddPouryaKitCqrs scanned for the Module, or for the application when it is null, in the order first
    // scanned: the dispatcher runs their operations (OperationTypes.Dispatchable), and no other registration's.
    internal IEnumerable<Assembly> DispatchingAssembliesOf(Type? module) =>
        _dispatching.Where(scanned => scanned.Module == module).Select(scanned => scanned.Assembly);
}
