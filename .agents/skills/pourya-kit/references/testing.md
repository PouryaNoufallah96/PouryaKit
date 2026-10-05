# Testing

`PouryaKit.Testing`: PostgreSQL in a container, data reset between tests, Operations sent through a Gateway, waiting for and asserting deliveries, reading and replaying dead letters. Namespace `PouryaKit.Testing`, on xUnit v3; the fixture needs Docker.

- [The fixture](#the-fixture)
- [Each test](#each-test)
- [The Operation Context in tests](#the-operation-context-in-tests)
- [Time](#time)
- [Deliveries](#deliveries)
- [Dead letters](#dead-letters)

One fixture for the test assembly and a test against the hosted application, after the Proving Sample:

```csharp
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModularMonolith.Host;
using PouryaKit.Testing;

[assembly: AssemblyFixture(typeof(AppFixture))]
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]

public sealed class AppFixture : HostFixture
{
    protected override IHost CreateHost(string connectionString)
    {
        var builder = ModularMonolithHost.CreateBuilder(
            ["--environment", "Development", $"--ConnectionStrings:app={connectionString}"]
        );
        builder.Services.UsePouryaKitTestDelivery();   // deliveries at test speed (below)
        builder.WebHost.UseTestServer();   // served in memory, for HTTP tests (web.md)
        builder
            .Services.AddAuthentication(TestAuthentication.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthentication>(TestAuthentication.SchemeName, null);
        return ModularMonolithHost.Build(builder);
    }
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

- Derive one `HostFixture` per test assembly and register it with `AssemblyFixture`. Its `CreateHost(connectionString)` builds the application's Host, not started, with that connection string for its Modules. Build it with the same builder and pipeline methods `Program` uses, so the tests run the real composition, with the container validated on build as `AddPouryaKitModules` does in every environment. A Host that serves HTTP is a `WebApplication`, which is an `IHost`; serve it in memory and give it a test authentication scheme, as above, to test it over HTTP ([web.md](web.md#testing-over-http)).
- Before the first test, the fixture runs PostgreSQL in a container, applies every Module's migrations to it, Receipt tables included, and then starts the application on it, so Wolverine finds every table when it starts. After the last test it stops both.
- `CreateHost` may add test-only Modules beside the application's with a second `builder.AddPouryaKitModules(...)` call.
- The test project references `PouryaKit.Testing` and the Host project, `Microsoft.AspNetCore.TestHost` for HTTP tests, and each Module grants it its internals: `<InternalsVisibleTo Include="ModularMonolith.Tests" />` in the Module's project.

## Each test

- Derive each test class from `HostTest<TFixture>`. Before each test it waits until every delivery an earlier test caused has finished, then deletes every row in every schema except the migrations history, and every stored delivery, so no test sees another's data or deliveries. Tests that share a fixture run one at a time: `CollectionBehavior.CollectionPerAssembly`.
- Send Operations with `Fixture.Gateway<TModule>()`, exactly as the application does, each in its own scope. Assert on the returned Result and its `Errors`; read rows back with your own SQL on `Fixture.ConnectionString`. `Fixture.Host` is the started Host.
- Test an endpoint with HTTP requests through `Fixture.Host.GetTestClient()`, authenticated as the caller the test names; see [web.md](web.md#testing-over-http).

## The Operation Context in tests

- A test that does not care who runs its Operations calls the Gateway without a context, as above: `SendAsync(command, ct)` and `QueryAsync(query, ct)` are extension members in `PouryaKit.Testing` (`GatewayTestExtensions`), so the file needs `using PouryaKit.Testing;`. Each call runs as a new Operation of `GatewayTestExtensions.TestActor`, the named system Actor `Actor.System("PouryaKit.Testing")`. Application code never references `PouryaKit.Testing`, so it always names its context.
- A test that needs a particular Actor, tenant or correlation creates the `OperationContext` and passes it to the Gateway's own methods:

```csharp
[Fact]
public async Task The_context_a_caller_passes_to_a_gateway_is_the_context_the_handler_reads()
{
    var context = new OperationContext(Actor.Person("ada"), tenant: "acme");

    var read = await Fixture
        .Gateway<StubModule>()
        .QueryAsync(new ReadOperationContextQuery(), context, TestContext.Current.CancellationToken);

    Assert.Equal(context, read.Value);
}
```

- A Module's Gateway refuses a command or query that a test-only Module owns. To run a test-only probe inside an application Module's Unit of Work, open a scope from `Fixture.Host.Services`, begin it with `BeginOperation(new OperationContext(GatewayTestExtensions.TestActor), typeof(CatalogModule))`, and send the probe through that scope's `IDispatcher`, as a delivery step would.

## Time

- For domain code the test runs directly, create a `TestClock` with `using` inside the test method; see [domain-primitives.md](domain-primitives.md).
- For Operations sent through a Gateway, register a `FakeTimeProvider` as the Host's `TimeProvider` in `CreateHost` (`builder.Services.AddSingleton<TimeProvider>(time)`) and advance it from the tests.

## Deliveries

A test of the deliveries one Operation causes, after the Proving Sample, whose Catalog announces each new product and publishes `ProductCreatedV1`, which Inventory and Billing each handle:

```csharp
[Fact]
public async Task A_new_product_is_announced_by_the_host_after_it_is_committed()
{
    var catalog = Fixture.Gateway<CatalogModule>();
    var category = Guid.CreateVersion7();
    var product = Guid.CreateVersion7();
    await catalog.SendAsync(new CreateCategoryCommand(category, "Lamps"), TestContext.Current.CancellationToken);

    var (created, deliveries) = await Fixture.WaitForDeliveriesAsync(
        () => catalog.SendAsync(new CreateProductCommand(product, category, "Lamp"), TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken
    );

    Assert.True(created.IsSuccess);
    Assert.Equal(
        [
            new Delivery("billing.product-created", "catalog.product-created.v1", false),
            new Delivery("catalog.product-created.announce-new-product", "catalog.product-created", false),
            new Delivery("catalog.product-created.publish-product-created", "catalog.product-created", false),
            new Delivery("inventory.product-created", "catalog.product-created.v1", false),
        ],
        deliveries
            .Where(delivery => delivery.WireName is "catalog.product-created" or "catalog.product-created.v1")
            .OrderBy(delivery => delivery.Subscription, StringComparer.Ordinal)
    );
}
```

The two `catalog.` deliveries are the Notification's, stored in the product's commit; the other two are the Integration Event's, one per subscription, stored in the commit of `publish-product-created`'s delivery. The wait includes deliveries that deliveries caused, and their order is not fixed, so the test sorts them. It keeps only these two wire names: the sample's product also stores a Snapshot delivery, not yet documented here (#18).

- In `CreateHost`, call `builder.Services.UsePouryaKitTestDelivery()` after `UsePouryaKitWolverine`, which the application's builder method calls, as the fixture above does. Deliveries then run at test speed: Solo durability, so a stopped Host's deliveries are recovered at once; a failed delivery retried twice, 100 ms apart (`TestDeliveryServiceCollectionExtensions.RetryDelays`), so three attempts, and then set aside as a dead letter; and the message store polled every 100 ms (`PollingTime`). Count attempts from the setting, `1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count`, rather than a literal.
- `Fixture.WaitForDeliveriesAsync(operation, ct)` runs the Operation, typically a Gateway call, waits until every delivery it caused is handled or set aside as a dead letter, including deliveries those deliveries caused, and returns the Operation's outcome with one `Delivery(Subscription, WireName, DeadLettered)` per delivery, in no particular order. A failing delivery stays pending through every retry, so the wait ends only once it is set aside, and it comes back with `DeadLettered` true. It fails with a `DeliveryAssertionException` when the Operation stored no delivery at all, so a test that waits never passes on nothing, and with a `TimeoutException` when deliveries are still pending after 30 seconds.
- After it returns, assert the handler's effect: the rows it wrote, such as Inventory's stock item, or what the Host's implementation of the port recorded.
- `Fixture.AssertNothingSentAsync(operation, ct)` runs the Operation and fails unless it stored no delivery: what a rolled-back Operation leaves. A delivery is stored in the Operation's own commit, so there is nothing to wait for.
- `Fixture.WaitUntilDeliveredAsync(ct)` waits until no stored delivery is pending, such as after a test made the store hand one out again. A dead letter marked for replay counts as pending until the store has moved it back and it is handled or set aside again.
- Between tests, the reset waits for every pending delivery, then deletes every stored delivery, dead letters included. The message store's own tables, such as its node records, stay: they belong to the running Host.
- A test that starts a second Host of the application, for example to prove that a Notification committed by a Host that died is delivered by the next, starts it with `HostFixture.StartHostAsync(host, ct)`, which applies the migrations first, and stops or disposes it before the test ends. Compose it with its own `RetryDelays` or `InboxStaleTime` to test a setting, keeping [the stale-time rule](messaging.md#the-stale-time-rule) unless the test is about breaking it.

## Dead letters

A test of a delivery that keeps failing, after the Proving Sample's tests. Their test-only Module publishes one Integration Event to two subscriptions, `Circulation.Archive` and `Circulation.Ledger`, and a test makes one handler return a failed Result for its note until it heals it:

```csharp
[Fact]
public async Task A_delivery_that_keeps_failing_is_set_aside_and_replayed_once_its_cause_is_fixed()
{
    var circulated = new NoteToCirculateDomainEvent($"note {Guid.CreateVersion7()}");
    Circulation.Fail(Circulation.Archive, circulated.Text);

    var (_, deliveries) = await Fixture.WaitForDeliveriesAsync(
        () =>
            Fixture
                .Gateway<StubModule>()
                .SendAsync(new WriteNoteRaisingCommand(circulated.Text, [circulated]), TestContext.Current.CancellationToken),
        TestContext.Current.CancellationToken
    );

    // Only the failing subscription's delivery is set aside, with the Errors of its failed Result.
    Assert.Contains(new Delivery(Circulation.Archive, "stub.note-circulated.v1", true), deliveries);
    Assert.Contains(new Delivery(Circulation.Ledger, "stub.note-circulated.v1", false), deliveries);
    var deadLetter = Assert.Single(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
    Assert.Equal(typeof(IntegrationEventHandlerFailedException).FullName, deadLetter.ExceptionType);
    Assert.Contains($"{Circulation.Refused.Code} ({Circulation.Refused.Message})", deadLetter.ExceptionMessage);

    // The cause is fixed, then the dead letter is replayed: only the archive runs again.
    Circulation.Heal(Circulation.Archive, circulated.Text);
    await Fixture.ReplayAsync(deadLetter, TestContext.Current.CancellationToken);

    Assert.Empty(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
    Assert.Single(Circulation.RunsOf(Circulation.Ledger, circulated.Text));
}
```

- `Fixture.DeadLettersAsync(ct)` reads every dead letter through Wolverine's dead-letter storage, as an application inspects it ([messaging.md](messaging.md#dead-letters)), each as a `DeadLetter(Id, Subscription, WireName, ExceptionType, ExceptionMessage)`: the subscription it was received at, the wire name it was stored under, and the exception of its last attempt. Each test starts with none, so these are what its Operations caused. For a failed Result the exception is `NotificationHandlerFailedException` or `IntegrationEventHandlerFailedException` (`PouryaKit.Messaging.Wolverine`), whose message names each Error as `Code (Message)`; for a handler that threw, the exception it threw.
- `Fixture.ReplayAsync(deadLetter, ct)` replays one dead letter through Wolverine, which moves it back to its subscription's queue, where only that subscription's handler runs, and waits until it is handled or set aside again. It fails with a `DeliveryAssertionException` when the delivery is not a dead letter, so a replay never passes on nothing.
- Assert what the replay changed: the handler's effect, and that the other subscriptions of the message did not run again.
