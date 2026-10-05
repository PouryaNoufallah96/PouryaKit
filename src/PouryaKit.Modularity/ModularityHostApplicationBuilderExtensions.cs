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
        /// Module is reached through its <see cref="IGatewayProcessor{TModule}"/>.
        /// </summary>
        public IHostApplicationBuilder AddPouryaKitModules(Action<ModulesBuilder> modules)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(modules);

            modules(new ModulesBuilder(builder.Services));

            builder.Services.TryAddScoped<ModuleTag>();
            // Added after every Composition Root, so it is the IUnitOfWork the dispatcher resolves.
            builder.Services.AddScoped(RouteUnitOfWork);
            return builder;
        }
    }

    private static IUnitOfWork RouteUnitOfWork(IServiceProvider services)
    {
        var module =
            services.GetRequiredService<ModuleTag>().Module
            ?? throw new InvalidOperationException(
                "The Unit of Work was needed in a scope that no Module's Gateway opened. Send the Operation through IGatewayProcessor<TModule>."
            );
        return services.GetKeyedService<IUnitOfWork>(module)
            ?? throw new InvalidOperationException(
                $"{module.Name} registers no Unit of Work. Declare its data in its Composition Root, for example with UsePostgres."
            );
    }
}
