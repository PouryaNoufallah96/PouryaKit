using System.Diagnostics;
using System.Text;
using S2.Billing;
using S2.Host;
using S2.Inventory;

namespace S2.Tests;

public sealed class RestartTests(PostgresServer server)
{
    /// <summary>
    /// A child Host publishes; Billing succeeds and Inventory fails with a one-minute cooldown before its retry, so
    /// Inventory's envelope is still pending when the child is killed with SIGKILL. A new Host recovers the dead node's
    /// envelopes (Balanced mode, short durability timers) and runs Inventory again, now succeeding. Billing is not
    /// run again.
    /// </summary>
    [Theory]
    [InlineData(DeliveryChoice.RoutedQueues)]
    [InlineData(DeliveryChoice.SeparatedQueues)]
    [InlineData(DeliveryChoice.DeliveryEnvelope)]
    public async Task A_handler_that_succeeded_is_not_run_again_after_the_host_is_killed(DeliveryChoice delivery)
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();

        await using (var child = ChildHost.Start(db.ConnectionString, delivery, productId))
        {
            await child.WaitForLineAsync("PUBLISHED", TimeSpan.FromSeconds(60));
            Assert.True(await Eventually.WaitAsync(async () =>
                (await db.InvocationsAsync(BillingModule.HandlerName, productId)).Count == 1 &&
                (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Count == 1 &&
                (await db.IncomingAsync()).Any(e => e.Subscription == BillingModule.Subscription && e.Status == "Handled"),
                TimeSpan.FromSeconds(30)));
            child.Kill();
        }

        var beforeRestart = await db.IncomingAsync();
        TestContext.Current.TestOutputHelper?.WriteLine("before restart: " + string.Join("; ", beforeRestart));
        Assert.Equal("Handled", beforeRestart.Single(e => e.Subscription == BillingModule.Subscription).Status);
        var pending = beforeRestart.Single(e => e.Subscription == InventoryModule.Subscription);
        Assert.Equal("Incoming", pending.Status);
        Assert.NotEqual(0, pending.OwnerId);

        using var host = await db.StartHostAsync(delivery);

        Assert.True(await Eventually.WaitAsync(async () =>
            (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Contains("Succeeded"), TimeSpan.FromSeconds(30)));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Equal(["FailedResult", "Succeeded"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
        Assert.Equal(["Succeeded"], await db.InvocationsAsync(BillingModule.HandlerName, productId));
        Assert.All(await db.IncomingAsync(), e => Assert.Equal("Handled", e.Status));
    }

    /// <summary>
    /// The rejected Option B variant: <c>EndpointFor(queue).SendAsync</c> through the outbox builds its envelope without
    /// the local-route fix-up (<c>MessageRoute.CreateForSending</c> sets <c>Incoming</c> and the node for local routes;
    /// <c>DestinationEndpoint.SendAsync</c> does not), so the row sits in the incoming table as <c>Outgoing</c> with
    /// owner 0. Recovery only selects <c>Incoming</c> rows with owner 0, so after a kill Inventory never runs again.
    /// </summary>
    [Fact]
    public async Task A_delivery_sent_with_EndpointFor_through_the_outbox_is_not_recovered_after_the_host_is_killed()
    {
        await using var db = await server.CreateDatabaseAsync();
        var productId = Guid.NewGuid();

        await using (var child = ChildHost.Start(db.ConnectionString, DeliveryChoice.DeliveryEnvelopeViaEndpointFor, productId))
        {
            await child.WaitForLineAsync("PUBLISHED", TimeSpan.FromSeconds(60));
            Assert.True(await Eventually.WaitAsync(async () =>
                (await db.InvocationsAsync(InventoryModule.HandlerName, productId)).Count == 1 &&
                (await db.IncomingAsync()).Any(e => e.Subscription == BillingModule.Subscription && e.Status == "Handled"),
                TimeSpan.FromSeconds(30)));
            child.Kill();
        }

        var pending = (await db.IncomingAsync()).Single(e => e.Subscription == InventoryModule.Subscription);
        Assert.Equal(("Outgoing", 0), (pending.Status, pending.OwnerId));

        using var host = await db.StartHostAsync(DeliveryChoice.DeliveryEnvelopeViaEndpointFor);
        await Task.Delay(TimeSpan.FromSeconds(15));

        Assert.Equal(["FailedResult"], await db.InvocationsAsync(InventoryModule.HandlerName, productId));
        Assert.Equal("Outgoing", (await db.IncomingAsync()).Single(e => e.Subscription == InventoryModule.Subscription).Status);
    }
}

/// <summary>The Spike Host running as a separate OS process.</summary>
internal sealed class ChildHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly Lock _lock = new();

    private ChildHost(Process process) => _process = process;

    public static ChildHost Start(string connectionString, DeliveryChoice delivery, Guid productId)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, "S2.Host.dll"), "publish-then-hang", connectionString, delivery.ToString(), productId.ToString() })
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
        lock (_lock) _output.AppendLine(line);
    }

    public async Task WaitForLineAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_lock)
            {
                if (_output.ToString().Split('\n').Any(l => l.TrimEnd() == marker)) return;
            }
            if (_process.HasExited) break;
            await Task.Delay(100);
        }
        lock (_lock) throw new TimeoutException($"child never printed {marker}; exited={_process.HasExited}\n{_output}");
    }

    /// <summary>SIGKILL on Unix: no shutdown hooks, no disposal, no ownership release.</summary>
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
