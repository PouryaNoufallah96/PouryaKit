using M2.Catalog;
using M2.Host;
using M2.Ledger;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Modularity;
using PouryaKit.Persistence.EFCore;

namespace M2.Tests;

// Item 15: the idempotency keys of a Marten Unit of Work expire under the Kit's PouryaKitIdempotencyOptions, as an EF
// Module's do, in a Host with Ledger alone and in one with Catalog beside it; an infinite retention keeps every key.
public sealed class IdempotencyRetentionTests(Postgres postgres) : SpikeTest(postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_key_reserved_longer_than_the_retention_ago_is_removed_and_a_newer_one_kept(bool withCatalog)
    {
        var host = await StartWithKeysAsync(withCatalog, TimeSpan.FromHours(1));

        await WaitUntilAsync("select count(*) = 1 from ledger.pouryakit_idempotency_keys");
        if (withCatalog)
        {
            await WaitUntilAsync("select count(*) = 1 from catalog.pouryakit_idempotency_keys");
        }

        Assert.Equal(["new"], (await Db.RowsAsync(ConnectionString, "select request_id from ledger.pouryakit_idempotency_keys")).Select(row => (string)row[0]!));
        if (withCatalog)
        {
            Assert.Equal(["new"], (await Db.RowsAsync(ConnectionString, "select request_id from catalog.pouryakit_idempotency_keys")).Select(row => (string)row[0]!));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_infinite_retention_keeps_every_key(bool withCatalog)
    {
        await StartWithKeysAsync(withCatalog, Timeout.InfiniteTimeSpan);

        await Task.Delay(TimeSpan.FromSeconds(1), Token);

        Assert.Equal(2, await CountAsync("select count(*) from ledger.pouryakit_idempotency_keys"));
        if (withCatalog)
        {
            Assert.Equal(2, await CountAsync("select count(*) from catalog.pouryakit_idempotency_keys"));
        }
    }

    // Two keys in each Module's store, the one of request "old" reserved ten years ago.
    private async Task<Microsoft.Extensions.Hosting.IHost> StartWithKeysAsync(bool withCatalog, TimeSpan retention)
    {
        var host = await StartAsync(
            new SpikeHostOptions
            {
                WithCatalog = withCatalog,
                Services = services =>
                    services.Configure<PouryaKitIdempotencyOptions>(options =>
                    {
                        options.Retention = retention;
                        options.CleanupInterval = TimeSpan.FromMilliseconds(100);
                    }),
            }
        );
        foreach (var request in new[] { "old", "new" })
        {
            await Ledger(host).SendAsync(new OpenAccountCommand(Guid.CreateVersion7(), Guid.CreateVersion7()), Context(requestId: request), Token);
            if (withCatalog)
            {
                await host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>().SendAsync(new TouchCommand(), Context(requestId: request), Token);
            }
        }

        await Db.ExecuteAsync(ConnectionString, "update ledger.pouryakit_idempotency_keys set reserved_at = now() - interval '10 years' where request_id = 'old'");
        if (withCatalog)
        {
            await Db.ExecuteAsync(ConnectionString, "update catalog.pouryakit_idempotency_keys set reserved_at = now() - interval '10 years' where request_id = 'old'");
        }

        return host;
    }
}
