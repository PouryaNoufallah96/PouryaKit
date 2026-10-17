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
    /// services null, the application. <c>AddPouryaKitModules</c> calls it on each Module's builder as it adds the
    /// Module. For a Kit package's registration.
    /// </summary>
    static Type? ModuleOf(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services is not IModuleServices module)
        {
            return null;
        }

        ComposedFeatures.In(services).Compose(services, module.Module);
        return module.Module;
    }

    /// <summary>
    /// <paramref name="module"/>, the Module a declaration names when the Host's composition makes it on the Module's
    /// behalf, on the Host's own services; naming it composes nothing, so a Module the Host never adds fails the Host's
    /// build. Throws when <paramref name="services"/> is the builder of another Module: a Composition Root declares only
    /// for its own Module, without naming it. For a Kit package's registration.
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
    /// Keeps <typeparamref name="TService"/> per Module, as each Module's Unit of Work is kept: in a Host that composes
    /// Modules, the one unkeyed <typeparamref name="TService"/> resolves the one keyed by the Module the scope's Operation
    /// runs in, null when that Module keeps none, and throws in a scope whose Operation names no Module; a Host without
    /// Modules keeps its unkeyed registration. The resolver replaces the unkeyed registration, never tried beside it, at
    /// once when the Host has composed a Module and whenever <c>AddPouryaKitModules</c> composes one, so the order of this
    /// call and that one, and a repeat of either, change nothing. For a Kit package's registration of a store the Unit of
    /// Work's transaction keeps, such as <see cref="IIdempotencyStore"/>, which it keys by the Module on a Module's
    /// builder and registers unkeyed on the Host's own services.
    /// </summary>
    static void RouteByModule<TService>(IServiceCollection services)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ComposedFeatures.In(services).Route(services, ServiceDescriptor.Scoped(typeof(TService), provider => Route<TService>(provider)!));
    }

    private static TService? Route<TService>(IServiceProvider services)
        where TService : class
    {
        var module =
            services.GetRequiredService<Operation>().Module
            ?? throw new InvalidOperationException(
                $"{typeof(TService).Name} was needed in a scope whose Operation names no Module. Send the Operation through IGatewayProcessor<TModule>, or begin the scope's Operation with its Module."
            );
        // Null when the Module keeps none: GetService then answers null, as for a service never registered.
        return services.GetKeyedService<TService>(module);
    }

    /// <summary>
    /// The Feature assemblies a Kit registration on <paramref name="services"/> scans, recorded for the Host's
    /// <see cref="HostComposition"/>: <paramref name="featureAssemblies"/>, in their order, when it names any; otherwise
    /// the assembly of the Module's definition, where a Module's Features live (ADR 0001), on a Module's builder or for
    /// <paramref name="module"/>, the Module a declaration of the Host's composition names (as
    /// <see cref="ModuleOf(IServiceCollection, Type)"/>, which it checks); and otherwise none. For a Kit package's
    /// registration.
    /// </summary>
    static Assembly[] FeatureAssembliesOf(IServiceCollection services, Assembly[] featureAssemblies, Type? module = null)
    {
        ArgumentNullException.ThrowIfNull(featureAssemblies);
        var owner = module is null ? ModuleOf(services) : ModuleOf(services, module);
        Assembly[] scanned = featureAssemblies is [] && owner is not null ? [owner.Assembly] : [.. featureAssemblies];
        if (owner is not null || scanned.Length > 0)
        {
            ComposedFeatures.In(services).Add(owner, scanned);
        }

        return scanned;
    }
}
