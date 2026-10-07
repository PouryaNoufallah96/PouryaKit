using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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

        /// <summary>
        /// Declares that <paramref name="module"/>, the Module's definition type, runs <typeparamref name="TCommand"/>,
        /// one of its own commands, as recurring work: at every occurrence of the cadence that
        /// <see cref="PouryaKitRecurringCommandOptions.Every"/> sets, configured through the returned builder, as an
        /// Operation of its own for the system Actor named <paramref name="systemActor"/>, with a new correlation, and
        /// with the command <paramref name="occurrence"/> builds for the occurrence's time. Occurrences fall on whole
        /// multiples of the cadence counted from the Unix epoch, on the system clock. <paramref name="name"/> is its
        /// durable queue and half of its Receipts' key, as a delayed command's is. The Kit keeps one occurrence stored
        /// at a time: a Host that starts stores the first one after now unless one is already stored, and each
        /// occurrence stores the next one in its own commit, so a stopped Host leaves it for the next Host to run. An
        /// occurrence whose Receipt is already recorded, whichever stored delivery carries it, applies nothing and stores
        /// no next occurrence, so two Hosts that each stored one end with one. Occurrences missed while no Host ran are
        /// not made up: the one stored runs late, then the next one after now. An occurrence that fails is retried, then
        /// set aside as a dead letter, and the next one is stored only when it is replayed or a Host starts. Feature code
        /// never schedules it with <see cref="IDelayedCommands"/>. Declare it once: declaring it again with another system Actor
        /// or occurrence factory, or as a delayed command too, throws, naming the declaration it already has. Throws here as
        /// <see cref="AddPouryaKitDelayedCommand"/> does; the Host fails to start, as for a delayed command, and when the
        /// cadence is not longer than zero.
        /// </summary>
        public OptionsBuilder<PouryaKitRecurringCommandOptions> AddPouryaKitRecurringCommand<TCommand>(
            string name,
            string systemActor,
            Func<DateTimeOffset, TCommand> occurrence,
            Type? module = null
        )
            where TCommand : Command
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(systemActor);
            ArgumentNullException.ThrowIfNull(occurrence);
            DelayedCommandDeclarations.In(services).Add(name, typeof(TCommand), module, new Recurrence(systemActor, occurrence));
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StartupCheck>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RecurringCommandStart>());
            return services
                .AddOptions<PouryaKitRecurringCommandOptions>(name.ToLowerInvariant())
                .Validate(
                    options => options.Every > TimeSpan.Zero,
                    $"The recurring command {name} has no cadence: set PouryaKitRecurringCommandOptions.Every, named {name.ToLowerInvariant()}, longer than zero, as its declaration's options builder binds it."
                )
                .ValidateOnStart();
        }
    }
}
