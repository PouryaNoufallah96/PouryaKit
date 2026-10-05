using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        /// a scoped service, or <c>Build</c> throws. A Host that later calls <c>ConfigureContainer</c> with another
        /// service provider factory replaces this one, and that validation with it.
        /// </summary>
        public IHostApplicationBuilder AddPouryaKitModules(Action<ModulesBuilder> modules)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(modules);

            modules(new ModulesBuilder(builder.Services));

            builder.Services.TryAddScoped<Operation>();
            // Added after every Composition Root, so it is the IUnitOfWork the dispatcher resolves.
            builder.Services.AddScoped(RouteUnitOfWork);
            builder.ConfigureContainer(
                new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true })
            );
            return builder;
        }
    }

    private static IUnitOfWork RouteUnitOfWork(IServiceProvider services)
    {
        var module =
            services.GetRequiredService<Operation>().Module
            ?? throw new InvalidOperationException(
                "The Unit of Work was needed in a scope whose Operation names no Module. Send the Operation through IGatewayProcessor<TModule>, or begin the scope's Operation with its Module."
            );
        return services.GetKeyedService<IUnitOfWork>(module)
            ?? throw new InvalidOperationException(
                $"{module.Name} registers no Unit of Work. Declare its data in its Composition Root, for example with UsePostgres."
            );
    }
}
