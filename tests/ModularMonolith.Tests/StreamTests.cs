using System.Diagnostics;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModularMonolith.Tests.StreamHost;
using ModularMonolith.Tests.Streams;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Messaging.Wolverine.Marten;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Testing;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;

namespace ModularMonolith.Tests;

// The Stream write model (ticket #131, ADR 0011): the Marten Unit of Work of the Stream Module Accounts, through its
// Gateway and the Kit's delivery step, against PostgreSQL. Each test composes its Hosts over a database of its own: one of
// Accounts alone, or the Proving Sample's with Accounts beside its EF Core Modules on one message store.
public sealed class StreamTests(StreamFixture streams) : IClassFixture<StreamFixture>, IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];
    private string _database = "";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await streams.CreateDatabaseAsync();

    [Fact]
    public async Task One_message_store_and_one_durability_agent_serve_both_write_models()
    {
        var host = await StartAsync(streams.Mixed(_database));
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();

        Assert.Equal(2, host.Services.GetServices<IMessageStore>().Count());
        Assert.Single(await runtime.Stores.FindAllAsync());
        Assert.Single(await runtime.Stores.AllKnownAgentsAsync());
        Assert.Equal("wolverine", ((IMessageDatabase)runtime.Storage).SchemaName);
    }

    [Fact]
    public async Task A_marten_store_integrated_in_another_schema_than_the_message_stores_fails_the_host_when_it_starts()
    {
        var host = Keep(streams.Mixed(_database, martenSchema: "marten_wolverine"));

        var refused = await Assert.ThrowsAsync<InvalidWolverineStorageConfigurationException>(() => HostFixture.StartHostAsync(host, Token));

        Assert.Contains("exactly one message store tagged as the 'main' store", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_marten_store_on_another_database_than_the_message_stores_fails_the_host_when_it_is_built()
    {
        var other = new NpgsqlConnectionStringBuilder(await streams.CreateDatabaseAsync());

        var refused = Assert.Throws<InvalidOperationException>(() => streams.Mixed(_database, martenConnection: "events", arguments: $"--ConnectionStrings:events={other}"));

        Assert.Contains(
            $"The Marten store of UsePouryaKitMarten writes over the connection string events, the database {other.Host}:{other.Port}/{other.Database}, but UsePouryaKitWolverine keeps the message store over the connection string app",
            refused.Message,
            StringComparison.Ordinal
        );
    }

    // The EF package's registration declares the store and the Marten one joins it; in the other order Wolverine would be
    // registered twice.
    [Fact]
    public void A_host_that_registers_its_marten_store_before_the_ef_message_store_fails_naming_the_order()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Development", $"--ConnectionStrings:app={_database}"]);
        builder.UsePouryaKitMarten(connectionName: "app");

        var refused = Assert.Throws<InvalidOperationException>(() =>
        {
            builder.UsePouryaKitWolverine(connectionName: "app");
        });

        Assert.Contains("call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore before UsePouryaKitMarten", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_envelope_commits_in_the_transaction_of_its_events_and_is_delivered_through_the_kits_routed_queue()
    {
        var account = Guid.CreateVersion7();
        var probe = new StreamCommitProbe();
        List<object[]>? atCommit = null;
        probe.OnCommit = async () =>
            atCommit ??= await RowsAsync(
                $"""
                select pg_xact_commit_timestamp(e.xmin), pg_xact_commit_timestamp(w.xmin), w.status, w.owner_id, w.received_at
                from events.mt_events e, wolverine.wolverine_incoming_envelopes w where e.stream_id = '{account}'
                """
            );
        var host = await StartAsync(streams.MartenOnly(_database, services => StreamCommitProbe.Install(services, probe)));

        var opened = await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);

        Assert.True(opened.IsSuccess);
        var row = Assert.Single(atCommit!);
        Assert.IsType<DateTime>(row[0]);
        Assert.Equal(row[0], row[1]);
        Assert.Equal("Incoming", row[2]);
        Assert.NotEqual(0, row[3]);
        Assert.Equal("local://accounts.welcome/", row[4]);
        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_that_moved_after_it_was_read_is_the_kits_conflict_and_appends_and_sends_nothing(bool whileTheFirstSaves)
    {
        var account = Guid.CreateVersion7();
        var probe = new StreamCommitProbe();
        var host = await StartAsync(streams.MartenOnly(_database, services => StreamCommitProbe.Install(services, probe)));
        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");

        // The first deposit holds after its handler read the stream, or at its save, past FetchForWriting; the second
        // commits meanwhile.
        var (reached, release) = Hold(probe, account, whileTheFirstSaves);
        var first = Accounts(host).SendAsync(new DepositCommand(account, 10), Context(), Token);
        await reached.Task.WaitAsync(Token);
        var second = await Accounts(host).SendAsync(new DepositCommand(account, 20), Context(), Token);
        release.SetResult();
        var refused = await first;

        Assert.True(second.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(refused.Errors));
        Assert.Equal(1L, await CountAsync(Events(account, "deposited")));
        Assert.Equal(1L, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://accounts.note-deposit/'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_started_twice_is_the_kits_conflict_and_appends_and_sends_nothing_the_second_time(bool whileTheFirstSaves)
    {
        var account = Guid.CreateVersion7();
        var probe = new StreamCommitProbe();
        var host = await StartAsync(streams.MartenOnly(_database, services => StreamCommitProbe.Install(services, probe)));
        var command = new OpenAccountCommand(account, Guid.CreateVersion7());

        Result first, second;
        if (whileTheFirstSaves)
        {
            // The first holds at its save, past FetchForWriting, while the second starts the same stream and commits.
            var (reached, release) = Hold(probe, account, whileTheFirstSaves: true);
            var running = Accounts(host).SendAsync(command, Context(), Token);
            await reached.Task.WaitAsync(Token);
            first = await Accounts(host).SendAsync(command, Context(), Token);
            release.SetResult();
            second = await running;
        }
        else
        {
            first = await Accounts(host).SendAsync(command, Context(), Token);
            second = await Accounts(host).SendAsync(command, Context(), Token);
        }

        Assert.True(first.IsSuccess);
        Assert.Equal(UnitOfWorkErrors.ConcurrencyConflict, Assert.Single(second.Errors));
        Assert.Equal(1L, await CountAsync(Events(account, "account_opened")));
        Assert.Equal(1L, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://accounts.welcome/'"));
    }

    // A child process commits through the Marten Unit of Work, prints COMMITTED from a store listener that runs after
    // Marten's commit and before Wolverine's send, and is killed there: the envelope stays with the dead node, and a Host
    // that starts later delivers it, as ADR 0004 records for the EF outbox.
    [Fact]
    public async Task A_notification_committed_before_the_process_is_killed_is_delivered_after_a_host_starts()
    {
        var account = Guid.CreateVersion7();
        using (var child = Process.Start(
                   new ProcessStartInfo("dotnet", [typeof(StreamHosts).Assembly.Location, "crash", _database, account.ToString()])
                   {
                       RedirectStandardOutput = true,
                       RedirectStandardError = true,
                       UseShellExecute = false,
                   }
               )!)
        {
            child.ErrorDataReceived += (_, _) => { };
            child.BeginErrorReadLine();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(120));
                string? line;
                do
                {
                    line = await child.StandardOutput.ReadLineAsync(timeout.Token);
                } while (line is not (null or "COMMITTED"));

                Assert.Equal("COMMITTED", line);
            }

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(Token);
        }

        Assert.Equal(1L, await CountAsync(Events(account, "account_opened")));
        Assert.Equal(1L, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://accounts.welcome/' and status = 'Incoming' and owner_id <> 0"));
        Assert.Equal(0L, await CountAsync(Events(account, "welcomed")));

        await StartAsync(streams.MartenOnly(_database));

        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");
        Assert.Equal(1L, await CountAsync("select count(*) from accounts.pouryakit_receipts where subscription = 'accounts.welcome'"));
    }

    [Fact]
    public async Task A_notification_handlers_stream_effect_receipt_publication_record_and_integration_event_commit_together()
    {
        var account = Guid.CreateVersion7();
        var probe = new StreamCommitProbe();
        List<object[]>? atCommit = null;
        probe.OnCommit = async () =>
        {
            if (atCommit is null && await CountAsync(Events(account, "welcomed")) == 1)
            {
                atCommit = await RowsAsync(
                    $"""
                    select pg_xact_commit_timestamp(xmin) from events.mt_events where stream_id = '{account}' and type = 'welcomed'
                    union all select pg_xact_commit_timestamp(xmin) from accounts.pouryakit_receipts where subscription = 'accounts.welcome'
                    union all select pg_xact_commit_timestamp(xmin) from accounts.pouryakit_receipts where subscription = 'accounts.account-opened.v1#published'
                    union all select pg_xact_commit_timestamp(xmin) from wolverine.wolverine_incoming_envelopes where received_at = 'local://accounts.account-opened/'
                    """
                );
            }
        };
        var host = await StartAsync(streams.MartenOnly(_database, services => StreamCommitProbe.Install(services, probe)));

        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("(select count(*) from accounts.pouryakit_receipts where subscription = 'accounts.account-opened') = 1");

        Assert.Equal(4, atCommit!.Count);
        Assert.All(atCommit, row => Assert.Equal(atCommit[0][0], Assert.IsType<DateTime>(row[0])));
    }

    // The welcome holds longer than the inbox stale time, so the store hands its delivery out again while the first run
    // still holds its Receipt: the duplicate's Receipt waits on the first one's row, then records nothing.
    [Fact]
    public async Task A_concurrent_duplicate_delivery_waits_for_the_first_receipt_and_never_runs_its_handler()
    {
        var account = Guid.CreateVersion7();
        AccountProbe.Hold(account, TimeSpan.FromSeconds(2));
        var host = await StartAsync(streams.MartenOnly(_database, StaleAfter(TimeSpan.FromMilliseconds(500))));

        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("(select count(*) from pg_stat_activity where wait_event_type = 'Lock' and query like 'insert into \"accounts\".pouryakit_receipts%') > 0");
        await WaitUntilAsync("(select count(*) from wolverine.wolverine_incoming_envelopes where status <> 'Handled') = 0");

        Assert.Equal(1, AccountProbe.RunsOf(account));
        Assert.Equal(1L, await CountAsync(Events(account, "welcomed")));
        Assert.Equal(1L, await CountAsync("select count(*) from accounts.pouryakit_receipts where subscription = 'accounts.welcome'"));
    }

    [Fact]
    public async Task A_failed_notification_handler_leaves_no_receipt_stream_effect_or_integration_event()
    {
        var account = Guid.CreateVersion7();
        AccountProbe.Fail(account);
        var host = await StartAsync(streams.MartenOnly(_database));

        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("(select count(*) from wolverine.wolverine_dead_letters where received_at = 'local://accounts.welcome/') = 1");

        // Every attempt ran the handler: no failed attempt's Receipt survived to skip the next one.
        Assert.Equal(TestDeliveryServiceCollectionExtensions.RetryDelays.Count + 1, AccountProbe.RunsOf(account));
        Assert.Equal(0L, await CountAsync(Events(account, "welcomed")));
        Assert.Equal(0L, await CountAsync("select count(*) from accounts.pouryakit_receipts"));
        Assert.Equal(0L, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://accounts.account-opened/'"));
    }

    [Fact]
    public async Task A_notification_handler_that_writes_nothing_keeps_its_receipt_and_a_redelivery_never_runs_it()
    {
        var account = Guid.CreateVersion7();
        var host = await StartAsync(streams.MartenOnly(_database));
        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");

        await Accounts(host).SendAsync(new DepositCommand(account, 10), Context(), Token);
        await WaitUntilAsync("(select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://accounts.note-deposit/' and status = 'Handled') = 1");
        var deposited = (Guid)(await RowsAsync($"select (data ->> 'id')::uuid from events.mt_events where stream_id = '{account}' and type = 'deposited'"))[0][0];

        Assert.Equal(1L, await CountAsync("select count(*) from accounts.pouryakit_receipts where subscription = 'accounts.note-deposit'"));
        await RowsAsync("update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0 where received_at = 'local://accounts.note-deposit/' returning 1");
        await WaitUntilAsync("(select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://accounts.note-deposit/' and status = 'Handled') = 1");
        Assert.Equal(1, AccountProbe.RunsOf(deposited));
    }

    [Fact]
    public async Task An_idempotent_command_that_changes_no_stream_keeps_its_key_and_replays_its_success()
    {
        var key = Guid.CreateVersion7();
        var host = await StartAsync(streams.MartenOnly(_database));

        var first = await Accounts(host).SendAsync(new PingCommand(key), Context(requestId: "ping-1"), Token);
        var replayed = await Accounts(host).SendAsync(new PingCommand(key), Context(requestId: "ping-1"), Token);

        Assert.Equal(first.Value, replayed.Value);
        Assert.Equal(1, AccountProbe.RunsOf(key));
        Assert.Equal(1L, await CountAsync("select count(*) from accounts.pouryakit_idempotency_keys where request_id = 'ping-1' and response is not null"));
    }

    [Fact]
    public async Task A_snapshot_delivered_into_a_stream_module_keeps_its_version_in_the_modules_own_table()
    {
        var host = await StartAsync(streams.Mixed(_database));
        var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
        var category = Guid.CreateVersion7();
        var product = Guid.CreateVersion7();
        await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), Token);

        await catalog.SendAsync(new CreateProductCommand(product, category, "Lamp"), Token);

        await WaitUntilAsync(
            $"(select count(*) from accounts.pouryakit_snapshot_versions where subscription = 'accounts.catalog-products' and aggregate_id = '{product}' and version = 1) = 1"
        );
        Assert.Equal(1L, await CountAsync("select count(*) from accounts.pouryakit_receipts where subscription = 'accounts.catalog-products'"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_key_reserved_longer_than_the_retention_ago_is_removed_and_a_newer_one_kept(bool mixed)
    {
        await StartWithKeysAsync(mixed, TimeSpan.FromHours(1));

        await WaitUntilAsync("(select count(*) from accounts.pouryakit_idempotency_keys) = 1");

        Assert.Equal(["new"], (await RowsAsync("select request_id from accounts.pouryakit_idempotency_keys")).Select(row => (string)row[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_infinite_retention_keeps_every_key(bool mixed)
    {
        await StartWithKeysAsync(mixed, Timeout.InfiniteTimeSpan);

        // A bounded wait for something that must not happen: ten cleanup intervals.
        await Task.Delay(TimeSpan.FromSeconds(1), Token);

        Assert.Equal(2L, await CountAsync("select count(*) from accounts.pouryakit_idempotency_keys"));
    }

    [Fact]
    public async Task A_send_that_fails_after_martens_commit_is_not_the_operations_failure_and_the_store_delivers_it()
    {
        var account = Guid.CreateVersion7();
        var host = await StartAsync(
            streams.MartenOnly(
                _database,
                services =>
                {
                    StaleAfter(TimeSpan.FromSeconds(1))(services);
                    FailingStreamSend.Install(services);
                }
            )
        );

        var opened = await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);

        Assert.True(opened.IsSuccess);
        Assert.Equal(1L, await CountAsync(Events(account, "account_opened")));
        Assert.Contains(streams.Logs.Entries, entry => entry.Level == LogLevel.Warning && entry.Exception?.Message == "The send after the commit failed.");
        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");
    }

    [Fact]
    public async Task A_typed_id_is_stored_as_the_kits_json_writes_it_and_a_stream_is_the_guid_its_typed_id_wraps()
    {
        var account = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();
        var host = await StartAsync(streams.MartenOnly(_database));

        await Accounts(host).SendAsync(new OpenAccountCommand(account, customer), Context(), Token);
        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");

        var stored = (string)(await RowsAsync($"select (data -> 'customer')::text from events.mt_events where stream_id = '{account}' and type = 'account_opened'"))[0][0];
        Assert.Equal(JsonSerializer.Serialize(new CustomerId(customer), new JsonSerializerOptions().UsePouryaKitJson()), stored);
        Assert.Equal(1L, await CountAsync($"select count(*) from events.mt_streams where id = '{account}'"));
        var read = await Accounts(host).QueryAsync(new GetAccountQuery(account), Context(), Token);
        Assert.Equal((account, customer, true, 2L), (read.Value.Id, read.Value.Customer, read.Value.IsWelcomed, read.Value.Version));
    }

    [Fact]
    public async Task A_stream_is_kept_and_delivered_under_the_operations_tenant_and_no_other_tenant_reads_or_writes_it()
    {
        var account = Guid.CreateVersion7();
        var host = await StartAsync(streams.MartenOnly(_database));

        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context("acme"), Token);
        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");
        var deposit = await Accounts(host).SendAsync(new DepositCommand(account, 10), Context("globex"), Token);

        Assert.Equal(["acme", "acme"], (await RowsAsync($"select tenant_id from events.mt_events where stream_id = '{account}'")).Select(row => (string)row[0]));
        Assert.True((await Accounts(host).QueryAsync(new GetAccountQuery(account), Context("acme"), Token)).IsSuccess);
        Assert.Equal(AccountErrors.NotFound, Assert.Single((await Accounts(host).QueryAsync(new GetAccountQuery(account), Context("globex"), Token)).Errors));
        Assert.Equal(AccountErrors.NotFound, Assert.Single((await Accounts(host).QueryAsync(new GetAccountQuery(account), Context(), Token)).Errors));
        Assert.Equal(AccountErrors.NotFound, Assert.Single(deposit.Errors));
    }

    // Conjoined streams are keyed by tenant and id, so one id is a stream of its own under each tenant; an Operation with
    // no tenant writes under Marten's default tenant.
    [Fact]
    public async Task One_stream_id_is_a_stream_of_its_own_under_each_tenant_and_no_tenant_is_martens_default()
    {
        var account = Guid.CreateVersion7();
        var host = await StartAsync(streams.MartenOnly(_database));
        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context("acme"), Token);

        var globex = await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context("globex"), Token);
        var none = await Accounts(host).SendAsync(new OpenAccountCommand(Guid.CreateVersion7(), Guid.CreateVersion7()), Context(), Token);

        Assert.True(globex.IsSuccess && none.IsSuccess);
        Assert.Equal(
            ["acme", "globex"],
            (await RowsAsync($"select tenant_id from events.mt_events where stream_id = '{account}' and type = 'account_opened' order by tenant_id")).Select(row => (string)row[0])
        );
        Assert.Equal(1L, await CountAsync("select count(*) from events.mt_events where tenant_id = '*DEFAULT*' and type = 'account_opened'"));
    }

    [Fact]
    public async Task A_tenant_named_as_martens_default_is_refused_by_a_command_and_by_a_query()
    {
        var host = await StartAsync(streams.MartenOnly(_database));

        var command = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Accounts(host).SendAsync(new OpenAccountCommand(Guid.CreateVersion7(), Guid.CreateVersion7()), Context("*DEFAULT*"), Token)
        );
        var query = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Accounts(host).QueryAsync(new GetAccountQuery(Guid.CreateVersion7()), Context("*DEFAULT*"), Token)
        );

        Assert.StartsWith("The Operation's tenant is *DEFAULT*", command.Message, StringComparison.Ordinal);
        Assert.StartsWith("The Operation's tenant is *DEFAULT*", query.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_that_changes_a_stream_marks_its_operation_rollback_only_and_nothing_commits()
    {
        var account = Guid.CreateVersion7();
        var host = await StartAsync(streams.MartenOnly(_database));
        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync($"({Events(account, "welcomed")}) = 1");

        var result = await Accounts(host).SendAsync(new DepositThroughQueryCommand(account), Context(), Token);

        Assert.Equal(OperationErrors.ReadingChangedState, Assert.Single(result.Errors));
        Assert.Equal(0L, await CountAsync(Events(account, "deposited")));
    }

    // A Stream's Id is settable, for Marten's fold: a query that points a tracked Stream at another stream of the same
    // version while its events are pending is refused (review finding F4 of #120).
    [Fact]
    public async Task A_query_that_points_a_tracked_stream_at_another_while_its_events_are_pending_is_refused_and_nothing_commits()
    {
        var account = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var host = await StartAsync(streams.MartenOnly(_database));
        await Accounts(host).SendAsync(new OpenAccountCommand(account, Guid.CreateVersion7()), Context(), Token);
        await Accounts(host).SendAsync(new OpenAccountCommand(other, Guid.CreateVersion7()), Context(), Token);
        await WaitUntilAsync("(select count(*) from events.mt_events where type = 'welcomed') = 2");

        var result = await Accounts(host).SendAsync(new DepositThenRedirectCommand(account, other), Context(), Token);

        Assert.Equal(OperationErrors.ReadingChangedState, Assert.Single(result.Errors));
        Assert.Equal(0L, await CountAsync("select count(*) from events.mt_events where type = 'deposited'"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // A Host that never started, or failed to, has nothing to stop.
            }

            if (host is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }
            else
            {
                host.Dispose();
            }
        }

        NpgsqlConnection.ClearAllPools();
    }

    // Two keys in Accounts' store, the one of request "old" reserved ten years ago; cleaned up every 100 ms.
    private async Task StartWithKeysAsync(bool mixed, TimeSpan retention)
    {
        void Retain(IServiceCollection services) =>
            services.Configure<PouryaKitIdempotencyOptions>(options =>
            {
                options.Retention = retention;
                options.CleanupInterval = TimeSpan.FromMilliseconds(100);
            });
        var host = await StartAsync(mixed ? streams.Mixed(_database, configure: builder => Retain(builder.Services)) : streams.MartenOnly(_database, Retain));
        foreach (var request in (string[])["old", "new"])
        {
            await Accounts(host).SendAsync(new OpenAccountCommand(Guid.CreateVersion7(), Guid.CreateVersion7()), Context(requestId: request), Token);
        }

        await RowsAsync("update accounts.pouryakit_idempotency_keys set reserved_at = now() - interval '10 years' where request_id = 'old' returning 1");
    }

    // The first command on the account holds after its handler read the stream, or at its save's first batch; released, it
    // goes on.
    private static (TaskCompletionSource Reached, TaskCompletionSource Release) Hold(StreamCommitProbe probe, Guid account, bool whileTheFirstSaves)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (whileTheFirstSaves)
        {
            var held = 0;
            probe.OnBeforeSave = async () =>
            {
                if (Interlocked.Exchange(ref held, 1) == 0)
                {
                    reached.SetResult();
                    await release.Task;
                }
            };
        }
        else
        {
            var gate = AccountProbe.Close(account);
            _ = gate.Reached.Task.ContinueWith(_ => reached.SetResult(), TaskScheduler.Default);
            _ = release.Task.ContinueWith(_ => gate.Opened.SetResult(), TaskScheduler.Default);
        }

        return (reached, release);
    }

    private static Action<IServiceCollection> StaleAfter(TimeSpan time) =>
        services => services.Configure<PouryaKitMessageStoreOptions>(store => store.InboxStaleTime = time);

    private async Task<IHost> StartAsync(IHost host)
    {
        Keep(host);
        await HostFixture.StartHostAsync(host, Token);
        return host;
    }

    private T Keep<T>(T host)
        where T : IHost
    {
        _hosts.Add(host);
        return host;
    }

    private static IGatewayProcessor<AccountsModule> Accounts(IHost host) => host.Services.GetRequiredService<IGatewayProcessor<AccountsModule>>();

    private static OperationContext Context(string? tenant = null, string? requestId = null) =>
        new(Actor.System("stream-tests"), tenant) { RequestId = requestId };

    private static string Events(Guid stream, string type) => $"select count(*) from events.mt_events where stream_id = '{stream}' and type = '{type}'";

    private Task<List<object[]>> RowsAsync(string sql) => Sql.RowsAsync(_database, sql);

    private async Task<long> CountAsync(string sql) => (long)(await RowsAsync(sql))[0][0];

    private Task WaitUntilAsync(string condition) => Sql.WaitUntilAsync(_database, condition, seconds: 30);
}
