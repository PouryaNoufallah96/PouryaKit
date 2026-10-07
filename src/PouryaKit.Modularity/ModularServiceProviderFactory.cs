using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;

namespace PouryaKit.Modularity;

// Builds the Host's validated container, after checking the Host's final registrations for what container validation
// cannot see: a command or query that declares an authorizer the Kit must register, such as a permission check. A
// Composition Root added later, in this AddPouryaKitModules call or another, may register the authorizer a command or
// query of another Module declares, so the check waits until the container is built.
internal sealed class ModularServiceProviderFactory(ServiceProviderOptions options) : IServiceProviderFactory<IServiceCollection>
{
    private readonly DefaultServiceProviderFactory _container = new(options);

    public IServiceCollection CreateBuilder(IServiceCollection services) => _container.CreateBuilder(services);

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
    {
        containerBuilder.EnsurePouryaKitDeclaredAuthorizers();
        return _container.CreateServiceProvider(containerBuilder);
    }
}
