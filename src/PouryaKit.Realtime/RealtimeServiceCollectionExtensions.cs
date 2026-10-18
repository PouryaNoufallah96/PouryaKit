using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PouryaKit.Cqrs;

namespace PouryaKit.Realtime;

/// <summary>Composes the Realtime Capability, server-sent events from Notification Handlers, on a Host's services.</summary>
public static class RealtimeServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Adds the <see cref="IRealtimePublisher"/> a Notification Handler pushes with, and the instance's one listener: a
        /// hosted service that holds a dedicated PostgreSQL connection over the connection string named
        /// <paramref name="connectionName"/> in the Host's configuration, outside every pool, never multiplexed and with
        /// KeepAlive, listens on it before the Host serves, and passes every push PostgreSQL delivers to the clients
        /// connected to this instance through an endpoint mapped with <c>MapPouryaKitRealtime</c>. When that connection
        /// is lost, it reconnects, waiting up to 30 seconds between attempts, listens again, and then sends every
        /// connected client a <see cref="RealtimeEvents.Gap"/>. Delivery to clients is at most once and not durable: the
        /// Outbox and the Receipts hold the durable state, and a client reads it through a query. With the Kit's
        /// composition checks (<c>AddPouryaKitCompositionChecks</c>), the Host's build fails when the configuration does
        /// not set the connection string, or sets it blank, and when EF Core connects a DbContext that is a Unit of Work
        /// with other settings than it, the password aside, whatever configured the DbContext, since a push is written
        /// on its Unit of Work's own connection. Called again with the same name, it adds nothing; with another, it
        /// throws.
        /// </summary>
        public IServiceCollection AddPouryaKitRealtime(string connectionName)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);

            if (services.Select(descriptor => descriptor.ImplementationInstance).OfType<RealtimeConnection>().FirstOrDefault() is { } listened)
            {
                return string.Equals(listened.Name, connectionName, StringComparison.OrdinalIgnoreCase)
                    ? services
                    : throw new InvalidOperationException(
                        $"Realtime already listens over the connection string {listened.Name}, so it cannot also listen over {connectionName}. A Host listens over one connection string."
                    );
            }

            services.AddSingleton(new RealtimeConnection(connectionName));
            services.AddSingleton<RealtimeClients>();
            services.AddHostedService<RealtimeListener>();
            services.AddScoped<IRealtimePublisher, RealtimePublisher>();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, RealtimeConnectionCheck>());
            return services;
        }
    }
}

/// <summary>The connection string the instance's listener listens over, as <c>AddPouryaKitRealtime</c> named it.</summary>
internal sealed record RealtimeConnection(string Name);
