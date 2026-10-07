using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Cqrs;

// Builds the Host's container validated in every environment, and registers in it the HostComposition the composition
// checks read: the Host's final registrations, which only the container build sees. A later ConfigureContainer replaces
// this factory, which removes no registration, so the check runner finds no HostComposition and names the call.
internal sealed class ValidatingServiceProviderFactory : IServiceProviderFactory<IServiceCollection>
{
    private readonly DefaultServiceProviderFactory _container = new(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    public IServiceCollection CreateBuilder(IServiceCollection services) => _container.CreateBuilder(services);

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
    {
        containerBuilder.AddSingleton(new HostComposition(containerBuilder));
        return _container.CreateServiceProvider(containerBuilder);
    }
}
