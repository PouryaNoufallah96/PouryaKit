using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

/// <summary>
/// A Host's final composition, as the Kit's container was built from it: every registration, the Modules it composed and
/// the Feature assemblies the Kit's registrations scanned for each of them and for the application's own services, named
/// or inferred (<see cref="IModuleServices"/>). The container registers it when <c>AddPouryaKitCompositionChecks</c>
/// installed it, and every <see cref="ICompositionCheck"/> reads it when the Host starts. A Module is among
/// <see cref="Modules"/> once a Kit registration is made on its builder, as every Composition Root that registers its
/// Features or declares messages does.
/// </summary>
public sealed class HostComposition
{
    private readonly OrderedDictionary<Type, IReadOnlyList<Assembly>> _modules = [];
    private readonly IReadOnlyList<Assembly> _application = [];

    internal HostComposition(IEnumerable<ServiceDescriptor> registrations)
    {
        Registrations = [.. registrations];
        var features = Registrations
            .Select(descriptor => descriptor.IsKeyedService ? null : descriptor.ImplementationInstance)
            .OfType<ComposedFeatures>()
            .FirstOrDefault();
        if (features is null)
        {
            return;
        }

        foreach (var (module, assemblies) in features.Modules)
        {
            _modules.Add(module, [.. assemblies]);
        }

        _application = [.. features.Application];
    }

    /// <summary>Every registration of the Host, in the order the container was built from them.</summary>
    public IReadOnlyList<ServiceDescriptor> Registrations { get; }

    /// <summary>The definition type of every Module the Host composed, in the order a Kit registration first named it.</summary>
    public IReadOnlyCollection<Type> Modules => [.. _modules.Keys];

    /// <summary>
    /// The Feature assemblies the Kit's registrations scanned for <paramref name="module"/>, or for the application's own
    /// services when it is null, in the order first scanned; none when no registration scanned any.
    /// </summary>
    public IReadOnlyList<Assembly> FeatureAssembliesOf(Type? module) =>
        module is null ? _application : _modules.GetValueOrDefault(module) ?? [];
}
