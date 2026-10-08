using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

// Builds the Host's container validated in every environment, then runs every composition check on the Host's final
// registrations, which only the container build sees, before Build returns: a built Host that is never started can
// still run Operations through a Gateway, so no check may wait for the start. The checks run on the built provider, so a
// check may resolve singletons. A later ConfigureContainer replaces this factory, which removes no registration, so the
// HostComposition it registers is missing and the start fails, naming the call (CompositionCheckRunner).
internal sealed class ValidatingServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
{
    private readonly DefaultServiceProviderFactory _container = new(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    public IServiceCollection CreateBuilder(IServiceCollection services) => _container.CreateBuilder(services);

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
    {
        var composition = new HostComposition(containerBuilder);
        containerBuilder.AddSingleton(composition);
        var provider = _container.CreateServiceProvider(containerBuilder);
        List<string> problems;
        try
        {
            problems = [.. provider.GetServices<ICompositionCheck>().SelectMany(check => check.Check(composition, provider))];
        }
        catch
        {
            // A check that throws fails the build as its problems do, so the container it resolved from is disposed too.
            Discard(provider);
            throw;
        }

        if (problems.Count == 0)
        {
            return provider;
        }

        Discard(provider);
        throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
    }

    // Disposes the container of a build that failed. The checks' problems, or a check's own exception, are what the Host
    // must see: a container that cannot be disposed, for a singleton that is only IAsyncDisposable or whose Dispose
    // throws, never replaces them.
    private static void Discard(IServiceProvider provider)
    {
        try
        {
            (provider as IDisposable)?.Dispose();
        }
        catch
        {
            // The caller throws what failed the build.
        }
    }
}
