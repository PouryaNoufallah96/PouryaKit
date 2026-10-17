using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using Wolverine;
using Wolverine.Postgresql;

namespace ModularMonolith.Tests;

// What a Host of another write model composes without the EF packages (ticket #121, ADR 0011): the store-neutral half of
// the message store from Messaging.Wolverine and the idempotency options from Cqrs. The one PostgreSQL call is the store
// package's, which this test makes itself, in a database of its own, where Wolverine creates its tables.
public sealed class MessageStoreWithoutEfTests(AppFixture app)
{
    private const string Schema = "wolverine";

    [Fact]
    public async Task A_host_starts_the_message_store_and_checks_the_idempotency_options_without_an_ef_registration()
    {
        var database = $"store_without_ef_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString;
        using var host = Compose(connectionString).Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var ef = host
            .Services.GetRequiredService<HostComposition>()
            .Registrations.SelectMany(descriptor =>
                new[]
                {
                    descriptor.ServiceType,
                    descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType,
                    (descriptor.IsKeyedService ? descriptor.KeyedImplementationInstance : descriptor.ImplementationInstance)?.GetType(),
                    (descriptor.IsKeyedService ? descriptor.KeyedImplementationFactory?.Method : descriptor.ImplementationFactory?.Method)?.DeclaringType,
                }
            )
            .OfType<Type>()
            .Select(type => type.Assembly.GetName().Name!)
            .Where(assembly => assembly.Contains("EntityFrameworkCore", StringComparison.Ordinal) || assembly.EndsWith(".EFCore", StringComparison.Ordinal))
            .Distinct();
        Assert.Empty(ef);
        Assert.Equal(
            [[1L]],
            await Sql.RowsAsync(
                connectionString,
                $"select count(*) from information_schema.tables where table_schema = '{Schema}' and table_name = 'wolverine_incoming_envelopes'"
            )
        );
    }

    // The options stop the start before Wolverine connects, so this Host names a database that does not exist.
    [Fact]
    public async Task Idempotency_options_out_of_range_stop_the_host_when_it_starts()
    {
        var builder = Compose("Host=db.invalid;Database=app");
        builder.Services.Configure<PouryaKitIdempotencyOptions>(options => options.BatchSize = 0);
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["The idempotency key BatchSize is 0; it must be at least 1."], thrown.Failures);
    }

    private static HostApplicationBuilder Compose(string connectionString)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(
            ["--environment", "Development", $"--ConnectionStrings:app={connectionString}"]
        );
        builder.UsePouryaKitMessageStore(
            "app",
            (wolverine, store) => wolverine.PersistMessagesWithPostgresql(store, Schema)
        );
        builder.Services.Configure<PouryaKitMessageStoreOptions>(store => store.Durability = DurabilityMode.Solo);
        builder.Services.AddPouryaKitIdempotencyOptions();
        return builder;
    }
}
