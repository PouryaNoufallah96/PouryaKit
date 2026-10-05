using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;
using PouryaKit.Validation;

namespace ModularMonolith.Tests;

// An application of one Module composes Cqrs, validation and its database directly on its services: no Modules, no
// Gateway, no ModuleBuilder. It opens a scope per Operation and begins the scope's Operation itself.
public sealed class SingleModuleTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_command_sent_through_the_dispatcher_commits_without_the_modularity_package()
    {
        await using var services = SingleModuleServices();
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module")));

        var written = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new WriteNoteCommand("solo"), TestContext.Current.CancellationToken);

        Assert.True(written.IsSuccess);
        Assert.Equal("solo", await Fixture.ScalarAsync<string>("select text from stub.notes"));
    }

    [Fact]
    public async Task A_query_sent_through_the_dispatcher_reads_without_the_modularity_package()
    {
        var id = await Fixture.ScalarAsync<long>("insert into stub.notes (text, version) values ('solo', 0) returning id");
        await using var services = SingleModuleServices();
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module")));

        var found = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .QueryAsync(new FindNotesQuery(id, "solo"), TestContext.Current.CancellationToken);

        Assert.Equal("solo", found.Value.ById?.Text);
        Assert.Equal(1, found.Value.Count);
    }

    private ServiceProvider SingleModuleServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new("ConnectionStrings:app", Fixture.ConnectionString)])
                .Build()
        );
        services.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        services.AddPouryaKitCqrs(typeof(StubDbContext).Assembly);
        services.AddPouryaKitValidation(typeof(StubDbContext).Assembly);
        return services.BuildServiceProvider();
    }
}
