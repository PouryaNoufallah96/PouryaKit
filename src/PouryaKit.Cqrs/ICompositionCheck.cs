namespace PouryaKit.Cqrs;

/// <summary>
/// One check of a Host's final composition that no single registration can make, such as a declared check whose
/// registration no Composition Root made. A Kit package implements it and registers it on the services its registration
/// receives, as a singleton with <c>TryAddEnumerable</c>, so it needs no Hosting reference. When a Host composed with
/// <c>AddPouryaKitCompositionChecks</c>, which <c>AddPouryaKitModules</c> calls, starts, the Kit runs every registered
/// check, in registration order, before any hosted service starts, and fails the start with one exception that names
/// every problem.
/// </summary>
public interface ICompositionCheck
{
    /// <summary>
    /// One sentence for each problem in <paramref name="composition"/>, naming its fix; none when there is none.
    /// <paramref name="services"/> is the Host's container, from which a check resolves singletons only.
    /// </summary>
    IEnumerable<string> Check(HostComposition composition, IServiceProvider services);
}
