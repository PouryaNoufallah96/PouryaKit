using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.Marten;

/// <summary>Composes the Host's one Marten store, which every Stream Module's Unit of Work writes in, on a Host's application builder.</summary>
public static class MartenHostApplicationBuilderExtensions
{
    /// <summary>The schema the Host's Marten store keeps its events and streams in, beside the Modules' schemas.</summary>
    public const string Schema = "events";

    extension(IHostApplicationBuilder builder)
    {
        /// <summary>
        /// Registers the Host's one Marten store over the connection string named <paramref name="connectionName"/>: events
        /// and streams in the schema <see cref="Schema"/>, stored in the Kit's JSON, so a typed ID is its value; conjoined
        /// tenancy, so every Stream belongs to the tenant of the Operation that wrote it; the Stream Modules' tables
        /// created or updated when the Host starts, and Marten's event tables when an event is first appended. Once for the
        /// Host, it adds the background service that removes the Stream Modules' expired idempotency keys, as
        /// <see cref="PouryaKitIdempotencyOptions"/> sets, and checks those options when the Host starts. Returns Marten's configuration, on which a messaging
        /// package's registration integrates the store, as <c>UsePouryaKitMarten</c> of
        /// <c>PouryaKit.Messaging.Wolverine.Marten</c> does; a Host with deliveries calls that one instead. Throws when the
        /// configuration has no such connection string or leaves it blank, and when the Host already has its Marten store.
        /// </summary>
        public MartenServiceCollectionExtensions.MartenConfigurationExpression AddPouryaKitMarten(string connectionName)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
            var connectionString = builder.Configuration.GetConnectionString(connectionName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    $"The configuration has no connection string named {connectionName} for the Marten store, or leaves it blank. Set ConnectionStrings:{connectionName}."
                );
            }

            if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(IDocumentStore)))
            {
                throw new InvalidOperationException("The Host already has its Marten store; a Host keeps one, which every Stream Module writes in.");
            }

            StreamModules.In(builder.Services);
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddHostedService<MartenIdempotencyKeyCleanup>();
            builder.Services.AddPouryaKitIdempotencyOptions();
            return builder
                .Services.AddMarten(options =>
                {
                    options.Connection(connectionString);
                    options.DatabaseSchemaName = Schema;
                    options.UseSystemTextJsonForSerialization(configure: json => json.UsePouryaKitJson());
                    options.Events.TenancyStyle = TenancyStyle.Conjoined;
                    options.Policies.AllDocumentsAreMultiTenanted();
                })
                .ApplyAllDatabaseChangesOnStartup();
        }
    }
}
