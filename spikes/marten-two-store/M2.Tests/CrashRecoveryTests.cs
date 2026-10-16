using System.Diagnostics;
using M2.Host;

namespace M2.Tests;

// Item 6: a child process commits an Operation through the Marten Unit of Work, prints COMMITTED from a store listener
// that runs after Marten's commit and before Wolverine's flush listener, and is killed there (SIGKILL). The envelope
// stays Incoming, owned by the dead node; a restarted Host delivers it, as ADR 0004 records for the EF outbox.
public sealed class CrashRecoveryTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_notification_committed_before_the_process_is_killed_is_delivered_after_restart()
    {
        var accountId = Guid.CreateVersion7();
        using var child = Process.Start(
            new ProcessStartInfo("dotnet", [typeof(SpikeHost).Assembly.Location, "crash", ConnectionString, accountId.ToString()])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }
        )!;
        child.ErrorDataReceived += (_, _) => { };
        child.BeginErrorReadLine();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (await child.StandardOutput.ReadLineAsync(timeout.Token) is { } line && line != "COMMITTED")
            {
            }
        }

        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync(Token);

        Assert.Equal(1, await CountAsync("select count(*) from events.mt_events where stream_id = $1 and type = 'account_opened'", accountId));
        Assert.Equal(1, await CountAsync("select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://ledger.welcome/' and status = 'Incoming' and owner_id <> 0"));
        Assert.Equal(0, await CountAsync("select count(*) from events.mt_events where type = 'welcomed'"));

        await StartAsync();

        await WaitUntilAsync("select count(*) = 1 from events.mt_events where stream_id = $1 and type = 'welcomed'", accountId);
        Assert.Equal(1, await CountAsync("select count(*) from ledger.pouryakit_receipts where subscription = 'ledger.welcome'"));
    }
}
