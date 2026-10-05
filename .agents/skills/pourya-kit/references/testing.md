# Testing

`PouryaKit.Testing`: PostgreSQL in a container, data reset between tests, Operations sent through a Gateway, delivery assertions. Namespace `PouryaKit.Testing`, on xUnit v3; the fixture needs Docker.

One fixture for the test assembly and a test against the hosted application, from the Proving Sample:

```csharp
using Microsoft.Extensions.Hosting;
using ModularMonolith.Host;
using PouryaKit.Testing;

[assembly: AssemblyFixture(typeof(AppFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

public sealed class AppFixture : HostFixture
{
    protected override IHost CreateHost(string connectionString) =>
        ModularMonolithHost
            .CreateBuilder(["--environment", "Development", $"--ConnectionStrings:app={connectionString}"])
            .Build();
}

public sealed class UnitOfWorkTests(AppFixture app) : HostTest<AppFixture>(app)
{
    [Fact]
    public async Task A_command_that_raises_no_domain_event_is_committed_though_its_handler_never_saves()
    {
        var id = Guid.CreateVersion7();

        var created = await Fixture
            .Gateway<CatalogModule>()
            .SendAsync(new CreateCategoryCommand(id, " Lamps "), TestContext.Current.CancellationToken);

        Assert.Equal(id, created.Value);
    }
}
```

## The fixture

- Derive one `HostFixture` per test assembly and register it with `AssemblyFixture`. Its `CreateHost(connectionString)` builds the application's Host, not started, with that connection string for its Modules. Build it from the same builder method `Program` uses, so the tests run the real composition; the Development environment makes the container check scopes and registrations when it is built.
- Before the first test, the fixture runs PostgreSQL in a container and hosts the application on it with every Module's migrations applied. After the last test it stops both.
- `CreateHost` may add test-only Modules beside the application's with a second `builder.AddPouryaKitModules(...)` call.
- The test project references `PouryaKit.Testing` and the Host project, and each Module grants it its internals: `<InternalsVisibleTo Include="ModularMonolith.Tests" />` in the Module's project.

## Each test

- Derive each test class from `HostTest<TFixture>`. Before each test it deletes every row in every schema except the migrations history, so no test sees another's data. Tests that share a fixture run one at a time: `CollectionBehavior.CollectionPerAssembly`.
- Send Operations with `Fixture.Gateway<TModule>()`, exactly as the application does, each in its own scope. Assert on the returned Result and its `Errors`; read rows back with your own SQL on `Fixture.ConnectionString`. `Fixture.Host` is the started Host.

## Time

- For domain code the test runs directly, create a `TestClock` with `using` inside the test method; see [domain-primitives.md](domain-primitives.md).
- For Operations sent through a Gateway, register a `FakeTimeProvider` as the Host's `TimeProvider` in `CreateHost` (`builder.Services.AddSingleton<TimeProvider>(time)`) and advance it from the tests.

*Not yet available*: delivery assertions (#15).
