using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace PouryaKit.Cqrs;

public static class CqrsHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Stops the Host before it serves when its composition holds a mistake the Kit can see. The Host's container is
        /// validated when it is built, in every environment: every registration must be constructible and no singleton
        /// may depend on a scoped service, or <c>Build</c> throws. When the Host starts, before any hosted service starts,
        /// every registered <see cref="ICompositionCheck"/> runs, and one exception names every problem and its fix:
        /// among them a command or query that declares an authorizer the Kit must register when none is, such as a
        /// <c>[RequiresPermission]</c> without <c>AddPouryaKitPermissions</c>; a cacheable query without
        /// <c>AddPouryaKitCaching()</c>; a command handler without a Unit of Work; and each package's own, such as a
        /// Module's declaration made without its Module in a Host of Modules. It also registers the dispatcher.
        /// <c>AddPouryaKitModules</c> calls it; a Host without Modules calls it after any <c>ConfigureContainer</c> of its
        /// own, since a later <c>ConfigureContainer</c> replaces the container it installs, which fails the start, naming
        /// the call. Calling it again changes nothing.
        /// </summary>
        public IHostApplicationBuilder AddPouryaKitCompositionChecks()
        {
            ArgumentNullException.ThrowIfNull(builder);
            CqrsServiceCollectionExtensions.AddDispatcher(builder.Services);
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, CompositionCheckRunner>());
            builder.ConfigureContainer(new ValidatingServiceProviderFactory());
            return builder;
        }
    }
}
