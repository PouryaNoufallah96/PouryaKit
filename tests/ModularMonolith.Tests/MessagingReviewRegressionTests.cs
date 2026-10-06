using System.Reflection;
using Catalog;
using Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;
using Wolverine;

namespace ModularMonolith.Tests;

public sealed class MessagingReviewRegressionTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_publication_requires_a_concrete_event_type(bool abstractClass)
    {
        var services = new ServiceCollection();

        var thrown = Assert.Throws<InvalidOperationException>(() =>
        {
            if (abstractClass)
            {
                services.Publish<AbstractEvent>("tests.review.v1");
            }
            else
            {
                services.Publish<IEvent>("tests.review.v1");
            }
        });

        Assert.Contains("is not a concrete class", thrown.Message);
    }

    [Fact]
    public void The_same_subscription_cannot_belong_to_two_modules()
    {
        var services = new ServiceCollection();
        services.Subscribe<TwinV1, TwinIntegrationEventHandler>("tests.review.twin", typeof(CatalogModule));
        services.Subscribe<TwinV1, TwinIntegrationEventHandler>("Tests.Review.Twin", typeof(CatalogModule));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(TwinIntegrationEventHandler));

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            services.Subscribe<TwinV1, TwinIntegrationEventHandler>("tests.review.twin", typeof(InventoryModule))
        );

        Assert.Contains("tests.review.twin", thrown.Message);
        Assert.Contains(nameof(CatalogModule), thrown.Message);
        Assert.Contains(nameof(InventoryModule), thrown.Message);
    }

    [Fact]
    public void An_undefined_numeric_actor_kind_cannot_continue_as_a_system_actor()
    {
        var context = new OperationContext(Actor.System("review"));
        var options = new DeliveryOptions().WithOperationContext(context);
        var envelope = new Envelope();
        foreach (var (name, value) in options.Headers)
        {
            envelope.Headers[name] = value;
        }

        // Exercise the transport's context reader without exposing this internal seam to Feature code.
        var read = typeof(OperationContextHeaderExtensions).GetMethod(
            "ContinuedOperationContext",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var continued = Assert.IsType<OperationContext>(read.Invoke(null, [envelope]));
        Assert.Equal(context.Actor, continued.Actor);
        envelope.Headers[OperationContextHeaders.ActorKind] = "999";

        var thrown = Assert.Throws<TargetInvocationException>(() => read.Invoke(null, [envelope]));

        Assert.Contains("undefined ActorKind", Assert.IsType<InvalidOperationException>(thrown.InnerException).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipts_and_snapshot_versions_write_in_a_schema_containing_a_quote(bool snapshot)
    {
        const string schema = "review\"quoted";
        var cancellationToken = TestContext.Current.CancellationToken;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Production", $"--ConnectionStrings:app={Fixture.ConnectionString}"]
        );
        builder.Services.UsePostgres<QuotedSchemaDbContext>("app", schema);
        builder.Services.AddPouryaKitCqrs(typeof(StubModule).Assembly);
        builder.Services.AddScoped(
            typeof(IReceipts),
            typeof(ReceiptTable).Assembly.GetType("PouryaKit.Messaging.Wolverine.EFCore.EfReceipts", throwOnError: true)!
        );
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("review")));
        var dbContext = scope.ServiceProvider.GetRequiredService<QuotedSchemaDbContext>();
        var migration = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        migration.EnsureSchema(schema);
        migration.CreateReceiptTable(schema);
        migration.CreateSnapshotVersionTable(schema);
        try
        {
            foreach (var command in dbContext.GetService<IMigrationsSqlGenerator>().Generate(migration.Operations, dbContext.Model))
            {
                await dbContext.Database.ExecuteSqlRawAsync(command.CommandText, cancellationToken);
            }

            var receipts = scope.ServiceProvider.GetRequiredService<IReceipts>();
            var messageId = Guid.CreateVersion7();
            await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
            {
                if (snapshot)
                {
                    Assert.True(await receipts.RecordVersionAsync("tests.review", "aggregate", 1, cancellationToken));
                    Assert.True(await receipts.RecordVersionAsync("tests.review", "aggregate", 2, cancellationToken));
                    Assert.False(await receipts.RecordVersionAsync("tests.review", "aggregate", 1, cancellationToken));
                }
                else
                {
                    Assert.True(await receipts.RecordAsync("tests.review", messageId, cancellationToken));
                    Assert.False(await receipts.RecordAsync("tests.review", messageId, cancellationToken));
                }

                await transaction.CommitAsync(cancellationToken);
            }

            await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var stored = new NpgsqlCommand(
                snapshot
                    ? "select version from \"review\"\"quoted\".pouryakit_snapshot_versions where subscription = 'tests.review' and aggregate_id = 'aggregate'"
                    : "select count(*) from \"review\"\"quoted\".pouryakit_receipts where subscription = 'tests.review' and message_id = @id",
                connection
            );
            if (!snapshot)
            {
                stored.Parameters.AddWithValue("id", messageId);
            }

            Assert.Equal(snapshot ? 2L : 1L, await stored.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            await dbContext.Database.ExecuteSqlRawAsync("drop schema if exists \"review\"\"quoted\" cascade", CancellationToken.None);
        }
    }

    private interface IEvent;

    private abstract class AbstractEvent;

    private sealed class QuotedSchemaDbContext(DbContextOptions<QuotedSchemaDbContext> options) : PouryaKitDbContext(options)
    {
        // Only the migration-created messaging tables are needed; the Stub's mappings belong to its own DbContext.
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("review\"quoted");
    }
}
