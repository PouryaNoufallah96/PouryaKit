using System.Diagnostics;
using SagaSpike.Host;

namespace SagaSpike.Tests;

// Item 7: a Host with a saga carries the saga's generated chain in its Release publish through the existing code
// generation step, the Proving Sample's (ADR 0004; scripts/packaging/validate.py, production_publish): codegen write,
// then a Release restore and publish. The published Host, in Production, loads the chain from its own assembly
// (TypeLoadMode.Static), carries no runtime compiler, and starts the saga.
public sealed class ReleasePublishTests(Postgres postgres) : SpikeTest(postgres)
{
    private static readonly string Project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../SagaSpike.Host"));

    [Fact]
    public async Task A_release_publish_carries_the_sagas_generated_chain_and_runs_it_in_production()
    {
        var published = Directory.CreateTempSubdirectory("saga-spike-publish-").FullName;
        var generated = Path.Combine(Project, "Internal", "Generated");
        try
        {
            await DotnetAsync("restore", Project, "-p:Configuration=Release");
            await DotnetAsync("build", Project, "-c", "Release", "--no-restore");
            await DotnetAsync("run", "--project", Project, "-c", "Release", "--no-build", "--no-launch-profile", "--", "codegen", "write");
            await DotnetAsync("publish", Project, "-c", "Release", "--no-restore", "-o", published);

            var order = Guid.CreateVersion7();
            var (exit, output) = await RunAsync(Path.Combine(published, "SagaSpike.Host.dll"), "published", "tenant-a", order.ToString());

            Assert.True(exit == 0, output);
            Assert.Contains("SAGA STARTED", output);
            Assert.Equal(1, await CountAsync("select count(*) from wolverine.fulfilmentsaga_saga where id = $1", $"tenant-a:{order}"));
            Assert.False(File.Exists(Path.Combine(published, "Wolverine.RuntimeCompilation.dll")));
            Assert.False(File.Exists(Path.Combine(published, "JasperFx.RuntimeCompiler.dll")));
            // The chain it ran is the one generated with the Kit's storage helper and guard.
            var chains = Directory.GetFiles(Path.Combine(generated, "WolverineHandlers"), "*.cs").Select(File.ReadAllText).ToList();
            Assert.Contains(chains, chain => chain.Contains("SagaSpike.Kit.SagaStorage.EnrollAndFetchSagaStorage<string, SagaSpike.Shipping.FulfilmentSaga>", StringComparison.Ordinal));
            Assert.Contains(chains, chain => chain.Contains("SagaSpike.Kit.SagaStorage.IsClaimed(", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(generated))
            {
                Directory.Delete(Path.Combine(Project, "Internal"), recursive: true);
            }

            Directory.Delete(published, recursive: true);
        }
    }

    private async Task DotnetAsync(params string[] arguments)
    {
        var (exit, output) = await ProcessAsync("dotnet", arguments);
        Assert.True(exit == 0, $"dotnet {string.Join(' ', arguments)}:\n{output}");
    }

    private Task<(int Exit, string Output)> RunAsync(string assembly, params string[] arguments) => ProcessAsync("dotnet", [assembly, .. arguments]);

    private async Task<(int Exit, string Output)> ProcessAsync(string file, string[] arguments)
    {
        ProcessStartInfo start = new(file, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Project,
        };
        start.Environment["ConnectionStrings__app"] = ConnectionString;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var errors = process.StandardError.ReadToEndAsync(Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await errors);
    }
}
