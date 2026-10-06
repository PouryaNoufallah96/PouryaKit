# Modules

`PouryaKit.Modularity`: the Composition Root, the Gateway and one scope per Operation, in one Host container. Namespace `PouryaKit.Modularity`. The Operation Context it requires lives in `PouryaKit.Cqrs`.

- [The Composition Root](#the-composition-root)
- [The Gateway](#the-gateway)
- [The Operation Context](#the-operation-context)
- [Which Module an Operation belongs to](#which-module-an-operation-belongs-to)
- [Startup checks](#startup-checks)
- [Asking another Module a question](#asking-another-module-a-question)
- [An application of one Module](#an-application-of-one-module)

A Module's Composition Root and the Host that composes it, after the Proving Sample:

```csharp
// Catalog/CatalogModule.cs: the Module's one public type
public sealed class CatalogModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.MapUniqueConstraint<CatalogDbContext>(ProductConfiguration.NameIndex, ProductErrors.NameInUse);   // see persistence.md
        module.AddPouryaKitDapper(connectionName: "app", typeof(CatalogModule).Assembly);   // see dapper-reads.md
        module.AddPouryaKitCqrs(typeof(CatalogModule).Assembly);
        module.AddPouryaKitValidation(typeof(CatalogModule).Assembly);
        module.AddPouryaKitNotificationHandlers(typeof(CatalogModule).Assembly, typeof(CatalogModule));   // see messaging.md
        module.Publish<ProductCreatedV1>("catalog.product-created.v1", typeof(CatalogModule));   // for other Modules; see messaging.md
        module.PublishSnapshot<Product, ProductSnapshotV1, ProductSnapshotMapper>("catalog.product-snapshot.v1", typeof(CatalogModule));   // likewise
        module.AddScoped<IProductRepository, ProductRepository>();   // see persistence.md, The repository
        module.AddScoped<ICatalogProducts, CatalogProducts>();       // see Asking another Module a question
        module.AddPouryaKitEndpoints("/catalog", typeof(CatalogModule).Assembly);   // see web.md
    }
}

// ModularMonolith.Host: one builder method, shared by Program and the tests; its Build method adds the pipeline (web.md)
public static WebApplicationBuilder CreateBuilder(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
    builder.Services.AddAuthorization();
    builder.Services.AddPouryaKitWeb();
    builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>().Add<InventoryModule>().Add<BillingModule>());
    builder.UsePouryaKitWolverine(connectionName: "app");   // the Modules' deliveries; see messaging.md
    return builder;
}
```

## The Composition Root

- Implement `IModuleDefinition` once per Module, as a public sealed class with a parameterless constructor. Everything else in the Module stays `internal`, except the ports its Host implements for it with the records they carry ([messaging.md](messaging.md#writing-a-notification-handler)) and the migrations `dotnet-ef` writes; a structure test rejects any other public type ([testing.md](testing.md#structure-tests)).
- `Configure(ModuleBuilder module)` declares everything the Module needs and offers: its data (`UsePostgres`, `MapUniqueConstraint`, `AddPouryaKitDapper`), its Features (`AddPouryaKitCqrs`, `AddPouryaKitValidation`), its Notification Handlers (`AddPouryaKitNotificationHandlers`), the Integration Events and Snapshots it publishes and the Integration Event Handlers it subscribes (`Publish`, `PublishSnapshot`, `Subscribe`), its endpoints (`AddPouryaKitEndpoints`) and its own services. `ModuleBuilder` is an `IServiceCollection` over the Host's one container, so every registration extension works on it.
- The Unit of Work registered on a Module's builder becomes that Module's own; every other registration goes into the Host's container as it is.
- The Host adds each Module once: `builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>().Add<InventoryModule>().Add<BillingModule>())`. That runs each Composition Root and registers the Module's Gateway.

## The Gateway

- `IGatewayProcessor<TModule>` is how anything outside a Module runs one of its Operations: `SendAsync(command, context, ct)` for a command, `QueryAsync(query, context, ct)` for a query, each returning the Result. Resolve it from the Host's services.
- The `OperationContext` is required. Each call is one Operation in a new scope, begun with that context and with the Module as its `Operation.Module`, and disposed when the call returns.
- Commands and queries stay `internal`, so a caller that builds one lives in the Module's assembly or is granted `InternalsVisibleTo`, as the Proving Sample's test project is. Another Module asks through the owning Module's Contracts instead.

A scheduled import, which is independent system work, creates a context for each Operation it runs:

```csharp
var catalog = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
var importer = Actor.System("catalog-import");

var lamps = await catalog.SendAsync(new CreateCategoryCommand(Guid.CreateVersion7(), "Lamps"), new OperationContext(importer), ct);
var created = await catalog.SendAsync(
    new CreateProductCommand(Guid.CreateVersion7(), lamps.Value, "Desk lamp"),
    new OperationContext(importer),
    ct
);
```

## The Operation Context

`OperationContext` (`PouryaKit.Cqrs`) is plain data about who and what caused one Operation:

- `OperationId`: a new version 7 Guid for each context created, so one context belongs to one Operation.
- `Actor`: `Actor.Person(id)`, a person as the trust boundary that authenticated them identifies them, or `Actor.System(name)`, a named system identity for independent work no person caused, such as a scheduled job. `Actor.Kind` says which.
- `Tenant`: the tenant or company the Operation runs for; null where the application has none.
- `CorrelationId`: shared by every Operation in one chain of work; a new context with none given correlates with its own `OperationId`.
- `CausationId`: the Operation or message that caused this one; null for the Operation that started the chain.

Starting and continuing work:

- A caller that starts work creates `new OperationContext(actor, tenant)` and passes it to the Gateway. Create one for each Operation: two calls with one context share an `OperationId`.
- Work one Operation causes runs with `context.Continue()`: the same Actor, tenant and correlation, the causing Operation's `OperationId` as its causation, and a new `OperationId`. A Module that answers another Module's question passes the asking Operation's `Continue()` to its own Gateway.
- A continuation is trusted (G.6): its Actor is not authenticated again, and the Kit adds no authorization of its own for it. An `IAuthorizer` registered for the command or query it runs still runs, with the original Actor, and can recognise a continuation by its `CausationId`.
- There is no anonymous or default Actor. `Actor.Person` and `Actor.System` refuse a blank id or name, the Gateway throws `ArgumentNullException` for a null context, and resolving `OperationContext` in a scope whose Operation was never begun throws `InvalidOperationException`. Name system work with `Actor.System("...")`.

Reading it:

- Inject `OperationContext` into a handler, an authorizer, a validator or a Domain Event Handler. Nested commands and queries and the Domain Event Handlers of an Operation run in its scope, so they read the same context. Read it only by injection, never from an ambient request.

```csharp
using PouryaKit.Cqrs;
using PouryaKit.Domain;

// Products/Features/RemoveProducts/: only system work removes products in bulk
internal sealed class RemoveProductsCommandAuthorizer(OperationContext context) : IAuthorizer<RemoveProductsCommand>
{
    public Task<Result> AuthorizeAsync(RemoveProductsCommand command, CancellationToken ct) =>
        Task.FromResult(context.Actor.Kind == ActorKind.System ? Result.Success() : ProductErrors.BulkRemoveForbidden);
}
```

Setting it:

- Whoever opens an Operation's scope begins it once, before anything in the scope runs, with `scope.ServiceProvider.BeginOperation(context, module)` (`PouryaKit.Cqrs`). The Gateway does this for every call, so Feature code never calls it; only an application of one Module calls it itself (below). Beginning a scope's Operation twice throws.
- At the HTTP boundary, the Web Capability begins each request's Operation with a context built from the authenticated caller, and the endpoint passes that context to the Gateway ([web.md](web.md#the-caller-and-the-actor)). The delivery of a Notification begins its scope with a continuation of the raising Operation's context and the subscribing Module, and the delivery of an Integration Event with a continuation of the publishing Notification Handler's delivery and the receiving Module ([messaging.md](messaging.md#what-the-kit-guarantees)).

## Which Module an Operation belongs to

- `Operation.Module` (`PouryaKit.Cqrs`, a `Type`) is the Module the scope's opener named; the Gateway names its own. It belongs to the scope, not to the context, because work an Operation causes may run in another Module. It routes each command to that Module's Unit of Work: a command sent through `IDispatcher` in a scope whose Operation names no Module throws `InvalidOperationException` in a Host of Modules.
- A command or query belongs to the Module whose Composition Root registers its handler. Its Gateway is the only one that runs it: sent through another Module's Gateway, it is refused with an `InvalidOperationException` naming both Modules, and one that no Module handles is refused too. Neither opens a scope.
- A Composition Root that registers, replaces or removes the handler of a command or query another Module owns fails startup, naming both Modules.

## Startup checks

- A Module added twice, in one `AddPouryaKitModules` call or across calls, throws naming it.
- A Module whose Composition Root registers the handler of a command but no Unit of Work throws while the Host is composed, naming the Module and the command (`… handles the command … but registers no Unit of Work. Declare its data in its Composition Root, for example with UsePostgres.`). A Module that only answers queries needs no Unit of Work.
- `AddPouryaKitModules` validates the container when the Host is built, in every environment: every registration must be constructible and no singleton may depend on a scoped service, or `Build()` throws an `AggregateException` naming the problem. A Host that later calls `ConfigureContainer` with another service provider factory replaces it, and with it both that validation and the declared-authorizer check below.
- When the Host is built, every command or query whose handler is registered and that declares an authorizer the Kit must register, such as a `[RequiresPermission]`, must have that authorizer registered by some Composition Root; another authorizer of the same command or query does not count. Otherwise `Build()` throws an `InvalidOperationException`, one line per command or query (`… declares RequiresPermissionAttribute, but its check is not registered, so it would run unchecked. Call AddPouryaKitPermissions with the assembly … in the Composition Root of ….`). Fix it by calling `module.AddPouryaKitPermissions(assembly)` in that Module's Composition Root ([security.md](security.md#declaring-a-permission)).
- A service that needs the Gateway of a Module the Host never added fails `Build()`, naming that Gateway.

## Asking another Module a question

Modules talk in the ways G.5 lists. Inside a Module, Domain Events. Across Modules, asynchronously by default: an Integration Event when a Module reacts to something that happened in another, and a Snapshot when it keeps its own copy of another's data. The owning Module's Notification Handler publishes an Integration Event after its change commits, and its Unit of Work stores a Snapshot in the commit of the change; the receiving Module applies either in an Operation of its own, as Inventory opens a stock item and Billing a price list for each product Catalog creates ([messaging.md](messaging.md#integration-events-between-modules)), and Inventory keeps its copy of Catalog's products ([messaging.md](messaging.md#snapshots-between-modules)). A synchronous question only when the asking Operation genuinely needs the answer before it can go on, such as a permission, membership or availability check. Never HTTP or gRPC between Modules of one Host.

The owning Module publishes the question as an interface in its Contracts project and serves it through its own Gateway. From the Proving Sample, where Catalog answers product questions:

```csharp
// Catalog.Contracts/Products/: public, plain types only
public interface ICatalogProducts
{
    /// <summary>The product with <paramref name="productId"/>, or null when Catalog has no such product.</summary>
    Task<ProductSummary?> FindProductAsync(Guid productId, CancellationToken cancellationToken = default);
}

public sealed record ProductSummary(Guid Id, string Name);

// Catalog/Products/Features/GetProduct/: registered scoped, so it receives the asking Operation's context
internal sealed class CatalogProducts(IGatewayProcessor<CatalogModule> catalog, OperationContext asking) : ICatalogProducts
{
    public async Task<ProductSummary?> FindProductAsync(Guid productId, CancellationToken cancellationToken = default)
    {
        var found = await catalog.QueryAsync(new GetProductQuery(productId), asking.Continue(), cancellationToken);
        if (found.IsSuccess) return new ProductSummary(found.Value.Id, found.Value.Name);

        // Only "no such product" is an answer; any other failure is a bug in the question.
        return found.Errors is [var error] && error == ProductErrors.NotFound
            ? null
            : throw new InvalidOperationException($"Catalog could not answer: {string.Join(", ", found.Errors.Select(e => e.Code))}.");
    }
}

// In Catalog's Composition Root: module.AddScoped<ICatalogProducts, CatalogProducts>();

// Ordering/Orders/Features/AddOrderLine/: Ordering references Catalog.Contracts, never Catalog
internal sealed class AddOrderLineCommandHandler(ICatalogProducts catalog, IRepository<Order, OrderId> orders)
    : CommandHandler<AddOrderLineCommand>
{
    public override async Task<Result> Handle(AddOrderLineCommand command, CancellationToken ct)
    {
        var product = await catalog.FindProductAsync(command.ProductId, ct);
        if (product is null) return OrderErrors.UnknownProduct;

        var order = await orders.FindAsync(new OrderId(command.OrderId), ct);
        if (order is null) return OrderErrors.NotFound;

        return order.AddLine(product.Id, product.Name);
    }
}
```

- Declare the interface and the types it returns in the owning Module's Contracts project, public, with plain values such as `Guid` and records of them, never the Module's internal types. The Proving Sample's `Catalog.Contracts` references no other project. Name its members as questions.
- Implement it in the owning Module, `internal sealed`, and register it scoped in its Composition Root. Resolved in the asking Operation's scope, it receives the asking `OperationContext` by injection. That scope belongs to the asking Module, so the implementation uses nothing of its own Module but its Gateway.
- Answer each question with a query sent through the Gateway with `asking.Continue()`. The answer is then its own Operation in its own scope, through the owning Module's full pipeline, and it changes nothing.
- Map the Result to the Contracts types. Turn only the expected failures into an answer, such as null for "not found"; any other failure is a bug in the question.
- The asking Module's project references the owning Module's Contracts project only, and injects the interface into its handler. A Contracts project references nothing from the Kit and no Module. A structure test rejects a Module that references another Module's implementation, and Contracts that reference the Kit or a Module, even when no code uses the reference ([testing.md](testing.md#structure-tests)).

## An application of one Module

A Host with a single Module may leave out `PouryaKit.Modularity`: it registers the Kit on its own services and opens a scope for each Operation, beginning it itself.

```csharp
// Composition: everything on the application's own services, with no AddPouryaKitModules
builder.Services.UsePostgres<NotesDbContext>(connectionName: "app", schema: "notes");
builder.Services.AddPouryaKitCqrs(typeof(NotesDbContext).Assembly);
builder.Services.AddPouryaKitValidation(typeof(NotesDbContext).Assembly);

// Each Operation: a new scope, begun with its context and no Module, then the dispatcher
await using var scope = services.CreateAsyncScope();
scope.ServiceProvider.BeginOperation(new OperationContext(Actor.System("nightly-notes")));
var written = await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(new WriteNoteCommand("solo"), ct);
```

- The one Unit of Work is the application's own, so `Operation.Module` stays null and nothing routes.
- Everything else holds as in a Host of Modules: one scope per Operation, the same pipeline, the same commit. There is no Gateway and no ownership check, and the Kit turns on no container validation: `HostApplicationBuilder` validates only in the Development environment.
