using System.Collections.Concurrent;
using System.Diagnostics;
using Inventory.Host;

namespace ModularMonolith.Tests.Fixtures;

// Inventory.Host run as its own OS process with `dotnet run`, configured through its environment, as a deployment
// configures it; its output is kept for a failing test's message. It runs in Production, where Wolverine loads its handler
// code instead of compiling it, as a Release build without the runtime compiler must: the Kit's handlers need none.
internal sealed class InventoryProcess : IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _output = [];

    private InventoryProcess(Process process) => _process = process;

    public string Output => string.Join(Environment.NewLine, _output);

    public static InventoryProcess Start(BrokerEnvironment environment)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "PouryaKit.slnx")))
        {
            root = root.Parent ?? throw new InvalidOperationException("The tests do not run inside the repository.");
        }

        // The configuration the tests were built in: Release under package validation.
        var configuration = typeof(InventoryHost).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>()
            .Single()
            .Configuration;
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "run", "--no-build", "--configuration", configuration, "--project", Path.Combine(root.FullName, "samples/ModularMonolith/src/Inventory.Host") },
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            Environment =
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["ConnectionStrings__app"] = environment.Inventory,
                [$"ConnectionStrings__{InventoryHost.BrokerConnection}"] = environment.Broker.ToString(),
                // Its HTTP server on a port of the system's choosing, so no other process's port is in the way.
                ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            },
        };
        var process = new InventoryProcess(Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start."));
        process._process.OutputDataReceived += (_, line) => process._output.Enqueue(line.Data ?? "");
        process._process.ErrorDataReceived += (_, line) => process._output.Enqueue(line.Data ?? "");
        process._process.BeginOutputReadLine();
        process._process.BeginErrorReadLine();
        return process;
    }

    // The address the Host's HTTP server bound, from the line the Host logs once it has started.
    public async Task<Uri> ListeningAsync(CancellationToken cancellationToken)
    {
        const string listening = "Now listening on: ";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        while (true)
        {
            if (_output.FirstOrDefault(line => line.Contains(listening, StringComparison.Ordinal)) is { } line)
            {
                return new Uri(line[(line.IndexOf(listening, StringComparison.Ordinal) + listening.Length)..].Trim());
            }

            Assert.False(timeout.IsCancellationRequested || _process.HasExited, $"Inventory.Host never listened:{Environment.NewLine}{Output}");
            await Task.Delay(100, cancellationToken);
        }
    }

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(TimeSpan.FromSeconds(10));
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
