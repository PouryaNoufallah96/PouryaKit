using ModularMonolith.Tests.Stub;
using Npgsql;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Persistence.Postgres;
using Wolverine.Runtime.Handlers;

namespace ModularMonolith.Tests;

// Ticket #58: outside Development a Kit Host loads Wolverine's handler code from its own assembly, the entry assembly,
// where codegen write generated it, and never compiles it at run time. A Wolverine handler a Host adds through the
// configure hook has a handler chain; without its generated type the Host does not start.
public sealed class CodeGenerationTests(AppFixture app)
{
    [Fact]
    public async Task A_host_outside_development_whose_handler_chain_has_no_generated_type_does_not_start()
    {
        var database = $"codegen_{Guid.CreateVersion7():N}";
        await app.RowsAsync($"create database {database}");
        var connectionString = new NpgsqlConnectionStringBuilder(app.ConnectionString) { Database = database }.ConnectionString;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(["--environment", "Production", $"--ConnectionStrings:app={connectionString}"]);
        // The Unit of Work the Kit's deliveries record their Receipts in: the Kit's container is validated when it is built.
        builder.Services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        builder.UsePouryaKitWolverine(connectionName: "app", configure: wolverine => wolverine.Discovery.IncludeType<PingedHandler>());
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<MissingPreBuiltTypesException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(
            $"could not be loaded from the configured ApplicationAssembly '{typeof(CodeGenerationTests).Assembly.GetName().Name}'",
            thrown.Message,
            StringComparison.Ordinal
        );
    }
}

// Public, as Wolverine's generated code names them.
public sealed record Pinged;

public sealed class PingedHandler
{
    public static void Handle(Pinged pinged) => ArgumentNullException.ThrowIfNull(pinged);
}
