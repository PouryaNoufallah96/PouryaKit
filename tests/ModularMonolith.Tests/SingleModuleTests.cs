using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Caching;
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

    [Fact]
    public async Task An_auditable_change_in_an_operation_nobody_began_throws_and_writes_nothing()
    {
        await using var services = SingleModuleServices();
        await using var scope = services.CreateAsyncScope();

        // A note is not auditable, so it needs no Actor; a folder is, and no Actor is ever made up for it.
        var note = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new WriteNoteCommand("solo"), TestContext.Current.CancellationToken);
        await using var folderScope = services.CreateAsyncScope();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            folderScope
                .ServiceProvider.GetRequiredService<IDispatcher>()
                .SendAsync(new CreateFolderCommand(Guid.CreateVersion7()), TestContext.Current.CancellationToken)
        );

        Assert.True(note.IsSuccess);
        Assert.StartsWith("No Operation Context was set", thrown.Message);
        Assert.Equal(0L, await Fixture.ScalarAsync<long>("select count(*) from stub.folders"));
    }

    // No Module's router refuses a scope never begun here, so the repository itself refuses the write in both scopes.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repository_write_in_a_scope_that_runs_no_unit_of_work_throws_and_saves_nothing(bool begun)
    {
        var kept = await Fixture.ScalarAsync<long>("insert into stub.notes (text, version) values ('kept', 0) returning id");
        await using var services = SingleModuleServices();
        Exception? added;
        Exception? removed;
        await using (var scope = services.CreateAsyncScope())
        {
            if (begun)
            {
                scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module")));
            }

            (added, removed) = await RepositoryTests.WriteOutsideAUnitOfWorkAsync(scope.ServiceProvider, kept);
        }

        Assert.Equal([["kept"]], await Fixture.RowsAsync("select text from stub.notes"));
        Assert.Equal(RepositoryTests.NoUnitOfWork, Assert.IsType<InvalidOperationException>(added).Message);
        Assert.Equal(RepositoryTests.NoUnitOfWork, Assert.IsType<InvalidOperationException>(removed).Message);
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
        services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        services.AddPouryaKitCqrs(typeof(StubDbContext).Assembly);
        services.AddPouryaKitCaching();
        services.AddPouryaKitValidation(typeof(StubDbContext).Assembly);
        return services.BuildServiceProvider();
    }
}
