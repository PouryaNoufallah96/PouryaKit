using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace PouryaKit.Cqrs;

public static class CqrsHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Stops the Host when its container is built, before anything runs, when its composition holds a mistake the Kit
        /// can see. The container is validated in every environment: every registration must be constructible and no
        /// singleton may depend on a scoped service, or <c>Build</c> throws an <c>AggregateException</c>. Then every
        /// registered <see cref="ICompositionCheck"/> runs on the built container, and <c>Build</c> throws one
        /// <see cref="InvalidOperationException"/> naming every problem and its fix: among them a command or query that
        /// declares an authorizer the Kit must register when none is, such as a <c>[RequiresPermission]</c> without
        /// <c>AddPouryaKitPermissions</c>; a cacheable query without <c>AddPouryaKitCaching()</c>; a command of the Host's
        /// Features without a Unit of Work; and each package's own, such as a Module's declaration made without its Module
        /// in a Host of Modules. What only the built application shows waits for the start: when the Host starts, before
        /// any hosted service starts, every registered <see cref="IStartupCheck"/> runs, such as Web's check that every
        /// endpoint was mapped, which the application does after <c>Build</c>, and <c>StartAsync</c> throws one
        /// <see cref="InvalidOperationException"/> in the same shape. It also registers the dispatcher.
        /// <c>AddPouryaKitModules</c> and <c>UsePouryaKitWolverine</c> call it; another Host calls it after any
        /// <c>ConfigureContainer</c> of its own, since a later <c>ConfigureContainer</c> replaces the container it
        /// installs, which skips the validation and both moments' checks, and the Host then fails when it starts, naming
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
