using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace S1.Tests;

/// <summary>Objective 2: a real second process is killed (SIGKILL) after commit; a restarted Host delivers.</summary>
public sealed class CrashRecoveryTests(PostgresServer server)
{
    private static readonly TimeSpan MarkerBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RecoveryBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultTimersRecoveryBudget = TimeSpan.FromMinutes(4);

    /// <param name="shortTimers">Shortened DurabilitySettings (see SpikeHost) versus Wolverine's defaults.</param>
    [Theory]
    [InlineData(Wolverine.DurabilityMode.Balanced, true)]
    [InlineData(Wolverine.DurabilityMode.Balanced, false)]
    [InlineData(Wolverine.DurabilityMode.Solo, false)]
    public async Task Message_committed_before_the_process_is_killed_is_delivered_after_restart(
        Wolverine.DurabilityMode durability, bool shortTimers)
    {
        await using var db = await server.CreateDatabaseAsync();
        var id = Guid.NewGuid();

        await using (var child = ChildHost.Start("commit-then-hang", db.ConnectionString, id, durability))
        {
            await child.WaitForLineAsync("COMMITTED", MarkerBudget);

            // Committed and not flushed: the rows are durable, nothing has been delivered.
            Assert.Equal(1, await db.ProductsAsync(id));
            var envelope = Assert.Single(await db.IncomingEnvelopesAsync());
            Assert.Equal(("Incoming", child.NodeNumber), (envelope.Status, envelope.OwnerId));
            Assert.Equal(0, await db.DeliveriesAsync(id));

            child.Kill();
        }

        Assert.Equal(0, await db.DeliveriesAsync(id));

        using var restarted = db.BuildHost(durability: durability, shortDurabilityTimers: shortTimers);
        var clock = Stopwatch.StartNew();
        await restarted.StartAsync(TestContext.Current.CancellationToken);
        var restartedNode = restarted.Services.GetRequiredService<IWolverineRuntime>().Options.Durability.AssignedNodeNumber;

        var budget = shortTimers ? RecoveryBudget : DefaultTimersRecoveryBudget;
        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 1, budget),
            $"the committed message was not delivered within {budget} after restart");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"durability={durability} shortTimers={shortTimers} restartedNode={restartedNode} " +
            $"delivered {clock.Elapsed.TotalSeconds:F1}s after restart began");
        Assert.Equal("Handled", (await db.IncomingEnvelopesAsync()).Single().Status);
        Assert.Equal(1, await db.DeliveriesAsync(id));
    }

    [Fact]
    public async Task Message_whose_handler_was_running_when_the_process_was_killed_is_handled_again_after_restart()
    {
        await using var db = await server.CreateDatabaseAsync();
        var id = Guid.NewGuid();

        await using (var child = ChildHost.Start("deliver-then-hang", db.ConnectionString, id, Wolverine.DurabilityMode.Balanced))
        {
            await child.WaitForLineAsync("STARTED", MarkerBudget);
            Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 1, MarkerBudget));
            child.Kill();
        }

        using var restarted = db.BuildHost();
        await restarted.StartAsync(TestContext.Current.CancellationToken);

        // At-least-once: the handler's side effect happened, its acknowledgment did not, so it runs again.
        Assert.True(await Eventually.WaitAsync(async () => await db.DeliveriesAsync(id) == 2, RecoveryBudget),
            "the interrupted message was not handled again after restart");
    }
}

/// <summary>The Spike Host running as a separate OS process.</summary>
internal sealed class ChildHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly object _lock = new();

    private ChildHost(Process process) => _process = process;

    public int NodeNumber { get; private set; } = -1;

    public static ChildHost Start(string scenario, string connectionString, Guid productId, Wolverine.DurabilityMode durability)
    {
        var hostDll = Path.Combine(AppContext.BaseDirectory, "S1.Host.dll");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { hostDll, scenario, connectionString, productId.ToString(), durability.ToString() })
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var child = new ChildHost(process);
        process.OutputDataReceived += (_, e) => child.Append(e.Data);
        process.ErrorDataReceived += (_, e) => child.Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return child;
    }

    private void Append(string? line)
    {
        if (line is null) return;
        lock (_lock)
        {
            _output.AppendLine(line);
            if (line.StartsWith("STARTED node=")) NodeNumber = int.Parse(line["STARTED node=".Length..]);
        }
    }

    public async Task WaitForLineAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_output.ToString().Split('\n').Any(l => l.TrimEnd().StartsWith(marker))) return;
            }
            if (_process.HasExited) break;
            await Task.Delay(100);
        }
        lock (_lock) throw new TimeoutException($"child never printed {marker}; exited={_process.HasExited}\n{_output}");
    }

    /// <summary>SIGKILL on Unix: no shutdown hooks, no disposal, no flush.</summary>
    public void Kill()
    {
        _process.Kill(entireProcessTree: true);
        _process.WaitForExit();
    }

    public ValueTask DisposeAsync()
    {
        if (!_process.HasExited) Kill();
        lock (_lock) TestContext.Current.TestOutputHelper?.WriteLine($"child output:\n{_output}");
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
