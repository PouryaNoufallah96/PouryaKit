using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

public static class DelayedCommandServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Declares that <paramref name="module"/>, the Module's definition type, schedules <typeparamref
        /// name="TCommand"/>, one of its own commands, to run later with <see cref="IDelayedCommands"/>, under
        /// <paramref name="name"/>: its durable queue and half of its Receipts' key, so it is stable; keep it when the
        /// type is renamed. The stored command names its type and is stored on the queue of its name, so renaming
        /// either strands the deliveries stored before it as dead letters. Its deliveries run in <paramref
        /// name="module"/>, so they reach its Unit of Work and its Receipts. A Module's Composition Root calls it on
        /// its builder; an application without Modules calls it on its own services with no Module. Throws here, at
        /// startup, when the type is abstract or generic, when the name is missing, invalid, a queue Wolverine keeps
        /// for itself or another delayed command's, ignoring case, and when the type is already declared under another
        /// name or for another Module. The Host then fails to start when a subscription has the same name, when no
        /// Outbox writes delayed commands (<see cref="INotificationOutbox"/>), or when its queue, as Wolverine built
        /// it, is not a durable application queue.
        /// </summary>
        public IServiceCollection AddPouryaKitDelayedCommand<TCommand>(string name, Type? module = null)
            where TCommand : Command
        {
            ArgumentNullException.ThrowIfNull(services);
            DelayedCommandDeclarations.In(services).Add(name, typeof(TCommand), module);
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupCheck>());
            return services;
        }
    }
}
