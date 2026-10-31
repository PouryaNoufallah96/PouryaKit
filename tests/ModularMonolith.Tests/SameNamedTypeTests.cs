using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.SameNamed;
using ModularMonolith.Tests.Stub;
using PouryaKit.Caching;
using PouryaKit.Cqrs;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// One application whose Features come from two assemblies, each holding a cacheable query and an idempotent command of
// one namespace-qualified name: the Stub Module's assembly and SameNamed. Its one cache and its one key table, in the
// stub schema of PostgreSQL, serve both, so only the key tells the two types apart. Each handler answers the name of its
// own assembly.
public sealed class SameNamedTypeTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private static readonly string Stub = typeof(StubDbContext).Assembly.GetName().Name!;

    private static readonly string SameNamed = SameNamedFeatures.Assembly.GetName().Name!;

    [Fact]
    public async Task Two_cacheable_queries_of_one_full_name_in_two_assemblies_each_get_their_own_handlers_answer()
    {
        await using var services = Services();
        var context = new OperationContext(Actor.Person("ada"), "north");
        var ours = new HandlingAssemblyQuery("lamp");
        var theirs = SameNamedFeatures.Query("lamp");

        var first = await RunAsync(
            services,
            context,
            dispatcher => dispatcher.QueryAsync(ours, TestContext.Current.CancellationToken)
        );
        var second = await RunAsync(
            services,
            context,
            dispatcher => dispatcher.QueryAsync(theirs, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ours.GetType().FullName, theirs.GetType().FullName);
        Assert.Equal(Stub, first.Value);
        Assert.Equal(SameNamed, second.Value);
    }

    [Fact]
    public async Task Two_idempotent_commands_of_one_full_name_in_two_assemblies_each_run_under_one_request_id()
    {
        await using var services = Services();
        var context = new OperationContext(Actor.Person("ada"), "north") { RequestId = "r1" };
        var ours = new NameHandlingAssemblyCommand();
        var theirs = SameNamedFeatures.Command();

        var first = await RunAsync(
            services,
            context,
            dispatcher => dispatcher.SendAsync(ours, TestContext.Current.CancellationToken)
        );
        var second = await RunAsync(
            services,
            context,
            dispatcher => dispatcher.SendAsync(theirs, TestContext.Current.CancellationToken)
        );

        Assert.Equal(ours.GetType().FullName, theirs.GetType().FullName);
        Assert.Equal(Stub, first.Value);
        Assert.Equal(SameNamed, second.Value);
        Assert.Equal(
            2L,
            await Fixture.ScalarAsync<long>(
                "select count(*) from stub.pouryakit_idempotency_keys where request_id = 'r1'"
            )
        );
    }

    // Each Operation in a scope of its own, as an application without Modules opens one.
    private static async Task<TResult> RunAsync<TResult>(
        ServiceProvider services,
        OperationContext context,
        Func<IDispatcher, Task<TResult>> run
    )
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(context);
        return await run(scope.ServiceProvider.GetRequiredService<IDispatcher>());
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection([new("ConnectionStrings:app", Fixture.ConnectionString)])
                .Build()
        );
        services.UsePouryaKitPostgres<StubDbContext>(connectionName: "app", schema: "stub");
        services.AddPouryaKitCqrs(typeof(StubDbContext).Assembly, SameNamedFeatures.Assembly);
        services.AddPouryaKitCaching();
        return services.BuildServiceProvider();
    }
}
