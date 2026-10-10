using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace PouryaKit.Messaging.Wolverine.RabbitMq;

/// <summary>
/// Composes the RabbitMQ Capability on a Host's application builder: the Integration Events and Snapshots the Host
/// sends to and receives from other Hosts.
/// </summary>
public static class RabbitMqHostApplicationBuilderExtensions
{
    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Delivers the Host's Integration Events and Snapshots to and from other Hosts through the RabbitMQ broker of the
        /// connection string named <paramref name="connectionName"/>, in addition to what <c>UsePouryaKitWolverine</c>
        /// delivers inside the Host. Each one a Module of the Host publishes is sent to a fanout exchange named by its wire
        /// name, written to the Outbox in the Unit of Work's commit whether or not a subscription of this Host receives it.
        /// The Host has one durable queue named <paramref name="serviceName"/>, which also becomes Wolverine's
        /// <c>ServiceName</c>, with a durable Inbox, bound to the exchange of each wire name the Host receives
        /// (<c>Receive</c>, <c>ReceiveSnapshot</c>) and a subscription of it subscribes to. Each envelope received there is
        /// sent again to the local queue of every subscription to it, and nowhere else, with its Operation Context,
        /// its identity and its trace state, and from there it is delivered, retried, dead-lettered and kept to one effect
        /// by its Receipt, as a delivery from a Module of the Host is. A delivery that fails before that, such as one of a
        /// wire name the Host no longer receives, is set aside in the message store as received at
        /// <c>rabbitmq://queue/{serviceName}</c>. No broker dead-letter queue is declared. The name is unique to one kind of
        /// Host on the broker: instances of the Host share the queue as competing receivers, and two different Hosts with
        /// one name would split its messages between them; nothing checks it. A message published before the queue of a
        /// receiving Host exists, so before its first start with that subscription, reaches no queue of it and is lost to
        /// it: start a Host that receives a wire name before the first publication it must receive. Bindings are declared
        /// and never removed: when the Host stops receiving a wire name, remove its binding from the queue. Throws here when
        /// the configuration has no such connection string or leaves it blank, and when <paramref name="serviceName"/> is
        /// missing or invalid.
        /// </summary>
        public IHostApplicationBuilder UsePouryaKitRabbitMq(string connectionName, string serviceName)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            if (!MessageNames.IsValid(serviceName))
            {
                throw new ArgumentException(
                    $"The service name {serviceName} names the Host's queue on the broker; use letters and digits in segments joined by single dots or hyphens, not beginning with amq., which RabbitMQ reserves.",
                    nameof(serviceName)
                );
            }

            // A blank one too: Wolverine applies nothing of it and says nothing, and the Host would dial the client's
            // default broker.
            var connectionString = builder.Configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"The configuration has no connection string named {connectionName} for the RabbitMQ broker, or leaves it blank. Set ConnectionStrings:{connectionName}."
                );
            }

            builder.Services.AddSingleton<IWolverineExtension>(services =>
                new RabbitMqExtension(connectionString, serviceName, services.GetRequiredService<IWireNames>())
            );
            return builder;
        }
    }
}
