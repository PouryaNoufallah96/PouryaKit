namespace PouryaKit.Cqrs;

/// <summary>
/// One check of a Host's final composition that no single registration can make, such as a declared check whose
/// registration no Composition Root made. A Kit package implements it and registers it on the services its registration
/// receives, as a singleton with <c>TryAddEnumerable</c>, so it needs no Hosting reference. When the container of a
/// Host composed with <c>AddPouryaKitCompositionChecks</c>, which <c>AddPouryaKitModules</c> calls, is built, the Kit
/// runs every registered check, in registration order, on the built container and before <c>Build</c> returns, so a
/// Host whose composition fails one never runs an Operation; <c>Build</c> throws one exception that names every problem.
/// A Host's checks run at two moments, with one report shape: these at the build, for everything the final
/// registrations show, and each <see cref="IStartupCheck"/> when the Host starts, before any hosted service starts, for
/// what only the built application shows, such as the routes it maps after <c>Build</c>.
/// </summary>
public interface ICompositionCheck
{
    /// <summary>
    /// One sentence for each problem in <paramref name="composition"/>, naming its fix; none when there is none.
    /// <paramref name="services"/> is the Host's container, from which a check resolves singletons, or scoped services from a scope of its own that it disposes; a check opens no connection.
    /// </summary>
    IEnumerable<string> Check(HostComposition composition, IServiceProvider services);
}
