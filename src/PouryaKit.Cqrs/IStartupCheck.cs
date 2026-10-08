namespace PouryaKit.Cqrs;

/// <summary>
/// One check of a Host that only the built application can make, such as an endpoint the application never mapped, since
/// it maps its routes after its container is built. A Kit package implements it and registers it as an
/// <see cref="ICompositionCheck"/> is registered. When a Host composed with <c>AddPouryaKitCompositionChecks</c> starts,
/// before any hosted service starts, the Kit runs every registered check, in registration order, and <c>StartAsync</c>
/// throws one exception that names every problem. A check whose answer the final registrations already hold is an
/// <see cref="ICompositionCheck"/>, which fails the Host earlier, when it is built.
/// </summary>
public interface IStartupCheck
{
    /// <summary>
    /// One sentence for each problem the started application would have, naming its fix; none when there is none.
    /// <paramref name="services"/> is the Host's container, from which a check resolves singletons, or scoped services from a scope of its own that it disposes; a check opens no connection.
    /// </summary>
    IEnumerable<string> Check(HostComposition composition, IServiceProvider services);
}
