---
name: pourya-kit
description: How to build a .NET modular monolith on PouryaKit. Use when writing, reviewing or testing code in a project that references PouryaKit packages, such as a Module, an aggregate, a command or query, a Domain Event Handler, a Dapper read, an Integration Event Handler or an endpoint.
---

# PouryaKit

PouryaKit lets Feature code hold only business behaviour. The Feature supplies the aggregate, the command or query, its validator and handler, its data access and its endpoint. The Kit dispatches the Operation, validates it, owns its single commit, runs its reactions and delivers its Integration Events and Snapshots to other Modules.

These hold in every Capability:

- A handler returns a Result. An expected business failure is an Error in that Result; exceptions are for bugs and infrastructure.
- The Unit of Work saves, commits and writes the Outbox. Feature code returns its Result and leaves all three to it.
- Anything outside a Module reaches it through its Gateway.
- Names follow the Kit's `GLOSSARY.md`: Module, Feature, Operation, Domain Event, Notification Handler, Integration Event, Snapshot.

## Capabilities

Each section below is filled by the ticket that delivers its Capability. A section that reads *Not yet available* has no API yet: work from the ticket it names, not from the package name.

### Domain primitives

`PouryaKit.Domain`: Result and Error, typed IDs, entities, value objects, aggregates, Domain Events and business rules. One namespace, `PouryaKit.Domain`; no dependencies.

An aggregate with a typed ID that checks a business rule, raises a Domain Event and returns a Result:

```csharp
using PouryaKit.Domain;

internal sealed record ProductId(Guid Value) : TypedId<Guid>(Value);

internal sealed record ProductCreatedDomainEvent(ProductId ProductId, string Name) : DomainEvent;

internal static class ProductErrors
{
    public static readonly Error NameRequired = Error.Validation("Product.NameRequired", "A product needs a name.");
    public static readonly Error NotFound = Error.NotFound("Product.NotFound", "The product does not exist.");
}

internal sealed class ProductNameMustNotBeBlank(string name) : IBusinessRule
{
    public Error Error => ProductErrors.NameRequired;

    public bool IsBroken() => string.IsNullOrWhiteSpace(name);
}

internal sealed class Product : AggregateRoot<ProductId>
{
    public string Name { get; private set; } = "";

    public static Result<Product> Create(ProductId id, string name)
    {
        var rule = CheckRule(new ProductNameMustNotBeBlank(name));
        if (rule.IsFailure) return rule.Errors;

        var product = new Product { Id = id, Name = name.Trim() };
        product.Raise(new ProductCreatedDomainEvent(id, product.Name));
        return product;
    }
}

// Elsewhere: var created = Product.Create(TypedId.New<ProductId>(), command.Name);
```

**Result and Error**

- Return a value or an Error directly; each converts to a Result. `Result.Success()` is the success without a value. When the value's static type is an interface (such as `IReadOnlyList<T>`), C# applies no implicit conversion, so return `Result.Success(value)`.
- Pass a failure on with `return result.Errors;`; it converts to a failed Result of any type and keeps every Error.
- Check `IsSuccess` or `IsFailure` before reading `Value`; reading it from a failure throws. No conversion turns a Result into its value, and a null value throws instead of becoming a success.
- Give each Error a stable code (`Product.NameRequired`), a message and a kind through its factory: `Validation`, `NotFound`, `Conflict`, `Forbidden` or `Unexpected`. Never an HTTP status; the Web package maps kinds to responses.
- Declare a Business Area's Errors once, as static fields.

**Typed IDs**

- Declare one per aggregate as a sealed record over `Guid`, `long`, `int` or `string`, as above. Two IDs are equal only when type and value are equal, so IDs of different aggregates cannot be mixed up.
- Create a new Guid ID with `TypedId.New<ProductId>()`; it is time-ordered (version 7). Wrap a value from input with `new ProductId(value)`.
- The constructor throws on an empty Guid or a blank string: that is a bug, so validate input before it reaches the constructor.
- Write no converter. JSON needs `TypedIdJsonConverterFactory` once in the serializer options. Until the Web (#14) and Messaging packages register it, add it to your serializer options yourself. It also handles typed IDs as dictionary keys, and reports an empty ID in JSON as a `JsonException`. EF Core needs nothing either: `PouryaKitDbContext` stores every typed ID as its value (see Persistence). Dapper registration arrives with #9.

**Aggregates, entities and value objects**

- Raise a Domain Event with `Raise` from inside the aggregate. Never clear Domain Events and never set `Version`: the Unit of Work owns both (#10, #11), and raising an event does not change the version.
- Entities are equal when type and ID are equal; do not override equality.
- Declare a value object as a record deriving from `ValueObject`. Its equality covers every member; a collection member compares by reference, so wrap it in a value object of its own.

**Domain Events**

- Declare each as a sealed record deriving from `DomainEvent`, named in the past tense with the `DomainEvent` suffix. Its `Id` and UTC `OccurredOn` are assigned once when it is created and survive serialization; never set them yourself.
- Carry typed IDs and values, never the aggregate itself or its version.
- Serialize a Domain Event by its runtime type (`JsonSerializer.Serialize(e, e.GetType(), options)`); serializing it as `DomainEvent` drops the derived members.
- A payload without `Id` deserializes with a new identity, so a stored or sent event must always include its `Id`.

**Business rules**

- Implement `IBusinessRule` with `IsBroken()` and the Error it returns. `CheckRule(rule)` in an entity, aggregate or value object returns a Result; a broken rule is a failed Result, never an exception.

**The clock**

- A Domain Event takes its `OccurredOn` from the running Operation's clock: the dispatcher binds the Host's registered `TimeProvider` as `DomainClock` for each Operation. Outside an Operation, `DomainClock` is the system clock.
- To change the time Operations see, the Host registers its own `TimeProvider` (`builder.Services.AddSingleton<TimeProvider>(time)`); `AddPouryaKitCqrs` adds `TimeProvider.System` only when none is registered.
- A business rule that compares with "now" takes the time as a parameter. The handler injects `TimeProvider` and passes `timeProvider.GetUtcNow()` in. Feature code takes time only from that `TimeProvider` and never binds `DomainClock`.

```csharp
internal sealed class SaleMustBeOpen(DateTimeOffset endsOn, DateTimeOffset now) : IBusinessRule
{
    public Error Error => Error.Conflict("Sale.Ended", "The sale has ended.");

    public bool IsBroken() => now >= endsOn;
}

// In the aggregate: public Result Buy(DateTimeOffset now) => CheckRule(new SaleMustBeOpen(EndsOn, now));
// In its handler, with TimeProvider timeProvider injected: var bought = sale.Buy(timeProvider.GetUtcNow());
```

**Testing**

- Test an aggregate through its public methods and assert on `DomainEvents` and on the returned Result's `Errors`.
- To fix the time of Domain Events an aggregate raises in a test, create a `TestClock` (`PouryaKit.Testing`) with `using` inside the test method itself: `using var clock = new TestClock(monday);`, then `clock.Time.Advance(...)`. A binding made in a constructor or fixture is not relied on to reach the test.

### Commands, queries and validation

`PouryaKit.Cqrs`, `PouryaKit.Validation`: commands and queries, their handlers, the fixed pipeline and validators. Namespaces `PouryaKit.Cqrs` and `PouryaKit.Validation`; validators are FluentValidation's `AbstractValidator<T>`.

A Feature with a command, its validator and its handler, and a query with its handler. Every type is `internal`:

```csharp
using FluentValidation;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

// Products/Features/CreateProduct/
internal sealed record CreateProductCommand(Guid ProductId, string Name) : Command<Guid>;

internal sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();      // checked before the typed-ID constructor can throw
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200).WithErrorCode("Product.NameTooLong");
    }
}

internal sealed class CreateProductCommandHandler(IRepository<Product, ProductId> products)
    : CommandHandler<CreateProductCommand, Guid>
{
    public override async Task<Result<Guid>> Handle(CreateProductCommand command, CancellationToken ct)
    {
        var product = Product.Create(new ProductId(command.ProductId), command.Name);
        if (product.IsFailure) return product.Errors;

        await products.AddAsync(product.Value, ct);
        return product.Value.Id.Value;             // no SaveChanges, no transaction, no Outbox
    }
}

// Products/Features/GetProduct/
internal sealed record GetProductQuery(Guid ProductId) : Query<ProductDto>;

internal sealed class GetProductQueryHandler(IProductReader reader) : QueryHandler<GetProductQuery, ProductDto>
{
    public override async Task<Result<ProductDto>> Handle(GetProductQuery query, CancellationToken ct) =>
        await reader.FindAsync(query.ProductId, ct) is { } product ? product : ProductErrors.NotFound;
}
```

`IRepository<,>` is the Kit's generic repository (`PouryaKit.Persistence`, see Persistence). `IProductReader` stands for the Feature's read access; Dapper reads arrive with #9.

**Commands and queries**

- Declare each as a sealed record: `Command<TResponse>` for a command with a value, `Command` for one without (its handler derives from `CommandHandler<TCommand>` and returns `Result`), `Query<TResponse>` for a read. Name it after its intent with the `Command` or `Query` suffix.
- Give each exactly one handler class deriving from `CommandHandler<,>`, `CommandHandler<>` or `QueryHandler<,>`. Keep both `internal`; the Kit finds them without `InternalsVisibleTo`. A command or query with no handler or several fails at startup.
- Commands, queries, handlers and authorizers are never generic type definitions (no `EchoQuery<T>`): registration rejects them at startup.
- A handler returns a Result and never saves, commits, begins a transaction or touches the Outbox: the Unit of Work does all of that once, for the whole Operation.
- A query reads. Its handler never sends a command: that throws, because a query runs without a Unit of Work.

**The pipeline**

- Every command and query passes the same fixed steps: logging, the authorization hook, validation, then the handler; a command's handler runs inside the Unit of Work. Never register a pipeline step or a behaviour yourself.
- Send from outside the Module through its Gateway (see Modules). Inside a handler, send a nested command with an injected `IDispatcher`: `await dispatcher.SendAsync(new ReserveStockCommand(id), ct)`. It joins the outer Unit of Work, so the Operation commits once.
- A nested command that fails, in its handler, its validation or its authorization, or by throwing, rolls the whole Operation back, even if your handler ignores its Result. The Operation then fails with that nested command's Errors. Pass a failure on with `return result.Errors;` instead of ignoring it.
- A failed nested query rolls nothing back; act on its Result as you need.
- One scope holds one Operation. Never send a second top-level command in a scope that already ran one, and never retry a failed command in the same scope: send it again through the Gateway, which opens a new scope for each call.

**Validation**

- Write one `AbstractValidator<T>` per command or query that needs input checks, beside it in the Feature, and keep it `internal`. Several validators for one type all run.
- Every failure of every validator comes back as one failed Result before the handler runs; nothing throws. Each failure is an Error of kind `Validation`, with FluentValidation's error code as `Code` (set your own with `WithErrorCode`) and the property name as `Target`.
- Validate input shape here. A business rule that depends on state belongs in the aggregate, through `CheckRule`.
- Without `PouryaKit.Validation`, the validation step does nothing.

**Authorization hook**

- The Actor is authenticated and authorized at the external entry point. Add a per-operation rule only when one command or query needs it: an `internal` class implementing `IAuthorizer<TOperation>` for that one concrete command or query, returning `Result.Success()` or a `Forbidden` Error. It runs before validation. Without one, the step does nothing. An authorizer for a base type, a marker interface or an open type fails at startup, because it would never run.
- An authorizer or a validator may run a query (a permission or existence lookup), but never sends a command: it runs before its command's handler, at every nesting depth, so that throws.

**Registration**

```csharp
module.AddPouryaKitCqrs(typeof(CatalogModule).Assembly);        // handlers and authorizers
module.AddPouryaKitValidation(typeof(CatalogModule).Assembly);  // validators
```

Make both calls on the Module's builder in its Composition Root (see Modules). The Unit of Work comes from the Module's persistence package, and logging from the Host.

**Testing**

- Test a Feature by sending it through its Module's Gateway against the hosted application (see Testing). Assert on the returned Result's `Errors`, never on exceptions, for business and validation failures.

### Modules

`PouryaKit.Modularity`: the Composition Root, the Gateway, Operation scopes and the Operation Context. Namespace `PouryaKit.Modularity`.

A Module's Composition Root and the Host that composes it, from the Proving Sample:

```csharp
// Catalog/CatalogModule.cs: the Module's one public type
public sealed class CatalogModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.AddPouryaKitCqrs(typeof(CatalogModule).Assembly);
        module.AddPouryaKitValidation(typeof(CatalogModule).Assembly);
    }
}

// ModularMonolith.Host: one builder method, shared by Program and the tests
public static HostApplicationBuilder CreateBuilder(string[] args)
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>());
    return builder;
}
```

**The Composition Root**

- Implement `IModuleDefinition` once per Module, as a public sealed class with a parameterless constructor. Everything else in the Module stays `internal`.
- `Configure(ModuleBuilder module)` declares everything the Module needs: its data (`UsePostgres`), its Features (`AddPouryaKitCqrs`, `AddPouryaKitValidation`) and its own services. `ModuleBuilder` is an `IServiceCollection` over the Host's one container, so every registration extension works on it.
- The Unit of Work registered on a Module's builder becomes that Module's own; every other registration goes into the Host's container as it is.
- The Host adds each Module once: `builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>().Add<BillingModule>())`. That runs each Composition Root and registers the Module's Gateway.

**The Gateway**

- `IGatewayProcessor<TModule>` is how anything outside a Module runs one of its Operations: `SendAsync(command)` for a command, `QueryAsync(query)` for a query, each returning the Result. Resolve it from the Host's services.
- Each call is one Operation in a new scope, tagged with the Module and disposed when the call returns. A command reaches its Module's Unit of Work only through that tag: sent through `IDispatcher` from a scope no Gateway opened, it throws `InvalidOperationException`.
- Commands and queries stay `internal`, so a caller that builds one lives in the Module's assembly or is granted `InternalsVisibleTo`, as the Proving Sample's test project is.

```csharp
var gateway = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
var created = await gateway.SendAsync(new CreateProductCommand(Guid.CreateVersion7(), "Lamp"), ct);
```

*Not yet available* (#13): the Operation Context, startup validation of each Module, and synchronous questions one Module asks another.

### Persistence and the Unit of Work

`PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore`, `PouryaKit.Persistence.Postgres`: repositories and specifications, a DbContext and schema per Module, Domain Event Handlers, versions and conflicts. A Module references `PouryaKit.Persistence.Postgres`, which brings the other two.

A Module's DbContext, a mapping and the aggregate it maps, from the Proving Sample:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

// Infrastructure/Data/Context/
internal sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : PouryaKitDbContext(options);

// Products/Data/Configurations/
internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.Property(product => product.Name).HasMaxLength(200);
    }
}

// Products/Entities/
internal sealed class Product : AggregateRoot<ProductId>
{
    private Product() { }   // for EF Core

    public string Name { get; private set; } = "";

    public static Result<Product> Create(ProductId id, string name) =>
        string.IsNullOrWhiteSpace(name) ? ProductErrors.NameRequired : new Product { Id = id, Name = name.Trim() };
}

// In the Composition Root: module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
// In a handler: inject IRepository<Product, ProductId> and call AddAsync, as in the Cqrs example above.
```

**The DbContext and its mappings**

- One DbContext per Module, `internal sealed`, deriving from `PouryaKitDbContext`, with an empty body. Its conventions store every typed ID declared in its assembly as its value, never map Domain Events, apply every `IEntityTypeConfiguration<T>` in its assembly, and make the Module's schema the default. Override `OnModelCreating` only to add to it, and call the base.
- Write one mapping per aggregate, beside its Business Area, stating only what is specific: table name, lengths, keys the database generates. Write no ID converter and no schema.
- A Guid typed ID created by the application (`TypedId.New<ProductId>()`) needs nothing more. A numeric typed ID the database generates needs `builder.Property(note => note.Id).ValueGeneratedOnAdd();` in its mapping, because conventions never make a converted key database-generated. Its `Id` stays null until the Unit of Work saves, after the handler has returned.

**PostgreSQL**

- `UsePostgres<TDbContext>(connectionName, schema)` on the Module's builder reads the connection string `ConnectionStrings:{connectionName}` from the Host's configuration, gives the Module its own schema with its own `__EFMigrationsHistory`, names tables and columns in snake_case, and makes the DbContext the Module's Unit of Work. Modules may share one connection string; each keeps its own schema.

**The repository**

- Inject `IRepository<TAggregate, TId>`: `AddAsync`, `Remove`, `FindAsync(id)`, and `FindAsync`, `ListAsync`, `AnyAsync`, `CountAsync` by an Ardalis.Specification `Specification<T>`. It tracks changes in the Operation's Unit of Work; nothing on it saves. Feature code reaches its data through it, never through the DbContext.

```csharp
internal sealed class NotesWithText : Specification<Note>
{
    public NotesWithText(string text) => Query.Where(note => note.Text == text).OrderBy(note => note.Id);
}
// var notes = await repository.ListAsync(new NotesWithText("draft"), ct);
```

**The Unit of Work**

- Each command Operation runs in one transaction that the Unit of Work owns. When the outermost handler returns a success and no nested command failed, it saves once and commits. A failed Result or a failed nested command rolls everything back; an exception rolls back and reaches the Gateway's caller.
- Nothing retries: no retrying execution strategy, no retry in the Unit of Work. To retry, send the command again through the Gateway.
- Today the Unit of Work leaves `Version` at 0 and handles no Domain Event: versions arrive with #11 and Domain Event Handlers with #10.

**Migrations**

- Each Module keeps its migrations in its own folder. The Host project references `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets="all"`, and the solution's tool manifest holds `dotnet-ef`. From the folder that holds `src/` (in the Proving Sample, `samples/ModularMonolith`):

```bash
dotnet ef migrations add Initial \
  --project src/Modules/Catalog/Catalog \
  --startup-project src/ModularMonolith.Host \
  --context CatalogDbContext \
  --output-dir Infrastructure/Data/Migrations
```

- `dotnet-ef` builds the Host as its `Program` does and finds the internal DbContext in its container, so the DbContext stays internal and needs no design-time factory. Name it with `--context`; set the folder with `--output-dir` and the namespace follows.
- The Host applies no migration at startup. `HostFixture` applies every Module's in tests.

*Not yet available*: Domain Event Handlers (#10); audit, soft delete, versions and conflicts (#11); specifications in depth and aggregate-specific repositories (#12).

### Dapper reads

`PouryaKit.Persistence.Dapper`: read queries in plain SQL on a pooled connection.

*Not yet available* (#9).

### Messaging

`PouryaKit.Messaging`, `PouryaKit.Messaging.Wolverine`, `PouryaKit.Messaging.Wolverine.EFCore`: Notification Handlers, Integration Events and Integration Event Handlers, Snapshots and Tombstones.

*Not yet available* (#15, #16, #17, #18).

### Web

`PouryaKit.Web`: endpoints beside their Feature, Result to HTTP, the Operation Context from the caller.

*Not yet available* (#14).

### Testing

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
    public async Task A_command_is_committed_though_its_handler_never_saves()
    {
        var id = Guid.CreateVersion7();

        var created = await Fixture
            .Gateway<CatalogModule>()
            .SendAsync(new CreateProductCommand(id, " Lamp "), TestContext.Current.CancellationToken);

        Assert.Equal(id, created.Value);
    }
}
```

**The fixture**

- Derive one `HostFixture` per test assembly and register it with `AssemblyFixture`. Its `CreateHost(connectionString)` builds the application's Host, not started, with that connection string for its Modules. Build it from the same builder method `Program` uses, so the tests run the real composition; the Development environment makes the container check scopes and registrations when it is built.
- Before the first test, the fixture runs PostgreSQL in a container and hosts the application on it with every Module's migrations applied. After the last test it stops both.
- `CreateHost` may add test-only Modules beside the application's with a second `builder.AddPouryaKitModules(...)` call.
- The test project references `PouryaKit.Testing` and the Host project, and each Module grants it its internals: `<InternalsVisibleTo Include="ModularMonolith.Tests" />` in the Module's project.

**Each test**

- Derive each test class from `HostTest<TFixture>`. Before each test it deletes every row in every schema except the migrations history, so no test sees another's data. Tests that share a fixture run one at a time: `CollectionBehavior.CollectionPerAssembly`.
- Send Operations with `Fixture.Gateway<TModule>()`, exactly as the application does, each in its own scope. Assert on the returned Result and its `Errors`; read rows back with your own SQL on `Fixture.ConnectionString`. `Fixture.Host` is the started Host.

**Time**

- For domain code the test runs directly, create a `TestClock` with `using` inside the test method; see Domain primitives.
- For Operations sent through a Gateway, register a `FakeTimeProvider` as the Host's `TimeProvider` in `CreateHost` (`builder.Services.AddSingleton<TimeProvider>(time)`) and advance it from the tests.

*Not yet available*: delivery assertions (#15).
