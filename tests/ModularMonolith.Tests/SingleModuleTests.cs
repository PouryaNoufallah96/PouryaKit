using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Persistence;
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

    // The save's tenant check reads the Operation's tenant only for a changed tenant-scoped entity (ticket #117, review
    // F3): a binder loaded and left unchanged beside a note's write needs no context, as the note alone does not.
    [Fact]
    public async Task A_tenant_scoped_aggregate_loaded_but_unchanged_in_an_operation_nobody_began_needs_no_context()
    {
        await using var services = SingleModuleServices();
        var binder = Guid.CreateVersion7();
        await using (var begun = services.CreateAsyncScope())
        {
            begun.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module"), "north"));
            Assert.True(
                (await begun.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(new CreateBinderCommand(binder, "North", []), TestContext.Current.CancellationToken)).IsSuccess
            );
        }

        await using var scope = services.CreateAsyncScope();
        var written = await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(new WriteNoteBesideBinderCommand(binder, "beside"), TestContext.Current.CancellationToken);

        Assert.True(written.IsSuccess);
        Assert.Equal(1L, await Fixture.ScalarAsync<long>("select count(*) from stub.notes where text = 'beside'"));
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

    // Ticket #68, the limit ADR 0003 records: the repository's read members need no Unit of Work, and a change to an
    // aggregate they loaded calls nothing of the Kit, so nothing can refuse it. The next three tests show what becomes
    // of that change; none of them is a guarantee.
    [Fact]
    public async Task A_change_to_a_loaded_aggregate_in_a_scope_that_sends_no_command_is_dropped_without_a_word()
    {
        await RelabelOutsideACommandAsync();

        Assert.Equal("grey", await Fixture.ScalarAsync<string>("select label_colour from stub.folders"));
    }

    // The tracked-state comparison's baseline is the query's start, so it already holds the change.
    [Fact]
    public async Task A_change_to_a_loaded_aggregate_is_not_refused_by_a_query_sent_after_it_and_is_still_dropped()
    {
        await RelabelOutsideACommandAsync(async dispatcher =>
            Assert.True((await dispatcher.QueryAsync(new FindNotesQuery(0, "none"), TestContext.Current.CancellationToken)).IsSuccess)
        );

        Assert.Equal("grey", await Fixture.ScalarAsync<string>("select label_colour from stub.folders"));
    }

    // The command writes a note, another row than the folder's: its commit saves the whole DbContext.
    [Fact]
    public async Task A_change_to_a_loaded_aggregate_is_saved_by_the_commit_of_a_command_sent_after_it()
    {
        await RelabelOutsideACommandAsync(async dispatcher =>
            Assert.True((await dispatcher.SendAsync(new WriteNoteCommand("its own"), TestContext.Current.CancellationToken)).IsSuccess)
        );

        Assert.Equal("blue", await Fixture.ScalarAsync<string>("select label_colour from stub.folders"));
        Assert.Equal("its own", await Fixture.ScalarAsync<string>("select text from stub.notes"));
    }

    // Creates a folder labelled grey with a command. Then a second begun scope, as an endpoint's is, finds the folder
    // through the repository and relabels it blue before it sends anything; what it sends after that is the caller's.
    private async Task RelabelOutsideACommandAsync(Func<IDispatcher, Task>? then = null)
    {
        var id = Guid.CreateVersion7();
        await using var services = SingleModuleServices();
        await using (var creating = services.CreateAsyncScope())
        {
            creating.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module")));
            var created = await creating
                .ServiceProvider.GetRequiredService<IDispatcher>()
                .SendAsync(new CreateFolderCommand(id), TestContext.Current.CancellationToken);
            Assert.True(created.IsSuccess);
        }

        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("single-module")));
        var folder = await scope
            .ServiceProvider.GetRequiredService<IRepository<Folder, FolderId>>()
            .FindAsync(new FolderId(id), TestContext.Current.CancellationToken);
        folder!.Relabel("blue");
        if (then is not null)
        {
            await then(scope.ServiceProvider.GetRequiredService<IDispatcher>());
        }
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
