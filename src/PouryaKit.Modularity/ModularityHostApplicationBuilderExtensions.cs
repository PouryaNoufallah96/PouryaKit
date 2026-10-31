using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;

namespace PouryaKit.Modularity;

/// <summary>
/// Composes the Modules Capability on a Host's application builder: each Module through its Composition Root, reached
/// through its Gateway.
/// </summary>
public static class ModularityHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Adds the Host's Modules, all in the Host's one container. Each Module's Composition Root runs once, and each
        /// Module is reached through its <see cref="IGatewayProcessor{TModule}"/>, in a scope whose Unit of Work, Receipts
        /// and idempotency store are its Module's own (<see cref="IModuleServices.RouteByModule{TService}"/>). It composes
        /// the Host's checks with <see cref="CqrsHostApplicationBuilderExtensions.AddPouryaKitCompositionChecks"/>: when the
        /// Host is built, in every environment, the container is validated and the Kit's composition checks stop it, such as on a
        /// <c>[RequiresPermission]</c> whose Module's Composition Root never called <c>AddPouryaKitPermissions</c>, or a
        /// declaration of a Module's messages made without its Module. A Host that later calls <c>ConfigureContainer</c>
        /// replaces the container the checks install, and fails when it starts, naming the call.
        /// </summary>
        public IHostApplicationBuilder AddPouryaKitModules(Action<ModulesBuilder> modules)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(modules);

            modules(new ModulesBuilder(builder.Services));

            builder.AddPouryaKitCompositionChecks();
            // Added after every Composition Root, so it is the IUnitOfWork the dispatcher resolves.
            builder.Services.AddScoped(RouteUnitOfWork);
            builder.Services.AddScoped<IReadingStateGuard>(services =>
                (RouteUnitOfWork(services, required: false) as IReadingStateGuard)!
            );
            return builder;
        }
    }

    private static IUnitOfWork RouteUnitOfWork(IServiceProvider services) =>
        RouteUnitOfWork(services, required: true)!;

    private static IUnitOfWork? RouteUnitOfWork(IServiceProvider services, bool required)
    {
        var module = services.GetRequiredService<Operation>().Module;
        if (module is null)
        {
            return required
                ? throw new InvalidOperationException(
                    "The Unit of Work was needed in a scope whose Operation names no Module. Send the Operation through IGatewayProcessor<TModule>, or begin the scope's Operation with its Module."
                )
                : null;
        }

        var unitOfWork = services.GetKeyedService<IUnitOfWork>(module);
        return unitOfWork is null && required
            ? throw new InvalidOperationException(
                $"{module.Name} registers no Unit of Work. Declare its data in its Composition Root, for example with UsePouryaKitPostgres."
            )
            : unitOfWork;
    }
}
