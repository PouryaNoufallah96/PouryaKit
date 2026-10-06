using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;

namespace PouryaKit.Modularity;

public static class ModularityHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Adds the Host's Modules, all in the Host's one container. Each Module's Composition Root runs once, and each
        /// Module is reached through its <see cref="IGatewayProcessor{TModule}"/>. The container is validated when the
        /// Host is built, in every environment: every registration must be constructible and no singleton may depend on
        /// a scoped service, or <c>Build</c> throws. <c>Build</c> also throws when a command or query whose handler is
        /// registered declares an authorizer the Kit must register and none was, such as a
        /// <c>[RequiresPermission]</c> whose Module's Composition Root never called <c>AddPouryaKitPermissions</c>.
        /// A Host that later calls <c>ConfigureContainer</c> with another service provider factory replaces this one,
        /// and both checks with it.
        /// </summary>
        public IHostApplicationBuilder AddPouryaKitModules(Action<ModulesBuilder> modules)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(modules);

            modules(new ModulesBuilder(builder.Services));

            builder.Services.AddPouryaKitCqrs();
            // Added after every Composition Root, so it is the IUnitOfWork the dispatcher resolves.
            builder.Services.AddScoped(RouteUnitOfWork);
            builder.Services.AddScoped<IReadingStateGuard>(services => (RouteUnitOfWork(services, required: false) as IReadingStateGuard)!);
            builder.ConfigureContainer(
                new ModularServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true })
            );
            return builder;
        }
    }

    private static IUnitOfWork RouteUnitOfWork(IServiceProvider services) => RouteUnitOfWork(services, required: true)!;

    private static IUnitOfWork? RouteUnitOfWork(IServiceProvider services, bool required)
    {
        var module = services.GetRequiredService<Operation>().Module;
        if (module is null)
        {
            return required ? throw new InvalidOperationException(
                "The Unit of Work was needed in a scope whose Operation names no Module. Send the Operation through IGatewayProcessor<TModule>, or begin the scope's Operation with its Module."
            ) : null;
        }

        var unitOfWork = services.GetKeyedService<IUnitOfWork>(module);
        return unitOfWork is null && required
            ? throw new InvalidOperationException(
                $"{module.Name} registers no Unit of Work. Declare its data in its Composition Root, for example with UsePostgres."
            )
            : unitOfWork;
    }
}
