using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Modularity;

// Builds the Host's validated container, after checking the Host's final registrations for what container validation
// cannot see: a command or query that declares an authorizer the Kit must register, such as a permission check.
internal sealed class ModularServiceProviderFactory(ServiceProviderOptions options) : IServiceProviderFactory<IServiceCollection>
{
    private readonly DefaultServiceProviderFactory _container = new(options);

    public IServiceCollection CreateBuilder(IServiceCollection services) => _container.CreateBuilder(services);

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
    {
        HostModules.In(containerBuilder).EnsureDeclaredAuthorizers(containerBuilder);
        return _container.CreateServiceProvider(containerBuilder);
    }
}
