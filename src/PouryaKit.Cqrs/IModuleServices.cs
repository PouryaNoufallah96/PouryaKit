using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

/// <summary>
/// The services a Module's Composition Root registers on: the Host container's services, seen from one Module, as
/// <c>PouryaKit.Modularity</c>'s <c>ModuleBuilder</c> implements them. A Kit registration made on them belongs to the
/// Module, so a Composition Root never names its own Module or its own assembly: a declaration whose deliveries run in a
/// Module, such as a subscription, runs in this one, and a registration that scans Features, such as
/// <c>AddPouryaKitCqrs()</c>, scans the assembly of this Module's definition unless it names assemblies. A registration on
/// the Host's own services, which belong to no Module, is the application's. The Kit tells the two apart at run time, so
/// a helper of the application typed as <see cref="IServiceCollection"/> keeps the Module it is given.
/// </summary>
public interface IModuleServices : IServiceCollection
{
    /// <summary>The definition type of the Module whose Composition Root registers on these services.</summary>
    Type Module { get; }

    /// <summary>
    /// The Module a Kit declaration made on <paramref name="services"/> without naming one belongs to: on a Module's
    /// builder its Module, which is recorded among the Host's <see cref="HostComposition.Modules"/>, and on the Host's own
    /// services null, the application. For a Kit package's registration.
    /// </summary>
    static Type? ModuleOf(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services is not IModuleServices module)
        {
            return null;
        }

        ComposedFeatures.In(services).Add(module.Module, []);
        return module.Module;
    }

    /// <summary>
    /// <paramref name="module"/>, the Module a declaration names when the Host's composition makes it on the Module's
    /// behalf, on the Host's own services. Throws when <paramref name="services"/> is the builder of another Module: a
    /// Composition Root declares only for its own Module, without naming it. For a Kit package's registration.
    /// </summary>
    static Type ModuleOf(IServiceCollection services, Type module)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(module);
        if (services is IModuleServices builder && builder.Module != module)
        {
            throw new InvalidOperationException(
                $"The Composition Root of {builder.Module.Name} declares for {module.Name}. A Composition Root declares only for its own Module and names none; the Host's composition names the Module of a declaration it makes on a Module's behalf."
            );
        }

        return ModuleOf(services) ?? module;
    }

    /// <summary>
    /// The Feature assemblies a Kit registration on <paramref name="services"/> scans, recorded for the Host's
    /// <see cref="HostComposition"/>: <paramref name="featureAssemblies"/>, in their order, when it names any; otherwise,
    /// on a Module's builder, the assembly of its Module's definition, where a Module's Features live (ADR 0001); and
    /// otherwise none. For a Kit package's registration.
    /// </summary>
    static Assembly[] FeatureAssembliesOf(IServiceCollection services, Assembly[] featureAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(featureAssemblies);
        var module = (services as IModuleServices)?.Module;
        Assembly[] scanned = featureAssemblies is [] && module is not null ? [module.Assembly] : [.. featureAssemblies];
        if (module is not null || scanned.Length > 0)
        {
            ComposedFeatures.In(services).Add(module, scanned);
        }

        return scanned;
    }
}
