# Multi-tenancy

Multi-tenancy is a registration across packages the Host already has, not a package: `ITenantScoped` (`PouryaKit.Domain`) marks an aggregate as one tenant's, `PouryaKitDbContext` and the Unit of Work (`PouryaKit.Persistence.EFCore`) read and save it only for the Operation's tenant, `AddPouryaKitTenancy` (`PouryaKit.Cqrs`) makes a Module, or an application without Modules, tenant-scoped, so that every command and query of it needs a tenant, and the delivery step (`PouryaKit.Messaging.Wolverine`) applies no delivery without one there. The tenant is `OperationContext.Tenant`, which the HTTP boundary takes only from a verified claim ([security.md](security.md#jwt-bearer-authentication), [web.md](web.md#the-caller-and-the-actor)).

- [Declaring a tenant-scoped Module](#declaring-a-tenant-scoped-module)
- [A tenant-scoped aggregate](#a-tenant-scoped-aggregate)
- [Reads](#reads)
- [Saves](#saves)
- [Deliveries](#deliveries)
- [What the Kit guarantees](#what-the-kit-guarantees)
- [Limits](#limits)
- [Testing](#testing)

## Declaring a tenant-scoped Module

The Proving Sample's Catalog, first in its Composition Root:

```csharp
public void Configure(ModuleBuilder module)
{
    // First, so the tenant requirement runs before every other authorizer of Catalog's commands and queries.
    module.AddPouryaKitTenancy();
    module.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
    // ...
}
```

- `AddPouryaKitTenancy()` registers the tenant requirement, an authorizer on the pipeline's authorization hook, for every concrete command and query of the assembly of the Module's definition, internal ones included, and for every one a handler there handles, wherever it is declared, such as in the Module's Contracts: every command and query the Module can dispatch. An Operation that was begun with no tenant fails with `TenantErrors.Required` (`Tenant.Required`, kind `Forbidden`) before its validator and handler run; `ToHttpResult()` answers it with 403 and the Error in the problem response's `errors`. The scope opener must begin the Operation ([ADR 0007](../../../../docs/adr/0007-operation-context-set-by-the-scope-opener.md)): in a scope nobody began, the requirement refuses with `Tenant.Required` too, unless another authorizer of the operation, such as a permission check, needs the Operation Context first and throws. It covers only the assemblies it is given: a Module whose `AddPouryaKitCqrs` scans another assembly too fails the Host when it is built (an assembly only another registration names, such as `AddPouryaKitLocalization`, makes nothing dispatchable and does not count), and the composition check `TenantRequirementCheck` names the Module, each command or query it would dispatch without a tenant, and the assembly to name to `AddPouryaKitTenancy`.
- It runs for a trusted continuation of one of those commands and queries too, unlike a permission check: a delayed command's run, a trusted peer's call, a command a Notification or Integration Event Handler sends during its delivery, or `Continue()` inside the asking Operation, as `ICatalogProducts` asks Catalog from another Module. Such a continuation runs with the tenant it carries, or is refused (spec #115; the one exception to G.6's "continuations are not authorized again", recorded in ADR 0007's #116 amendment). A delivery itself runs the Kit's own command, which is in no Feature assembly, so the requirement does not check it ([Limits](#limits)).
- An Operation's authorizers run in the order registered, so declare it before `AddPouryaKitCqrs` and `AddPouryaKitPermissions`: it then refuses a tenantless Operation before any authorizer of the Module's own reads data.
- An application without Modules declares it on its own services and names its Feature assemblies, as it does for `AddPouryaKitCqrs`: `services.AddPouryaKitTenancy(typeof(Program).Assembly)`. With no assembly there it throws `ArgumentException`, since it would cover nothing.
- The declaration is recorded for the Host: `HostComposition.IsTenantScoped(module)`, null for the application's own services, tells a Kit registration or composition check which Modules are tenant-scoped.
- Two declarations fail the Host when it is built in a tenant-scoped Module. A recurring command (`RecurringCommandTenancyCheck`): its occurrences run for a system Actor with no tenant, and the Kit runs none per tenant, so declare recurring work in a Module that is not tenant-scoped, as the Proving Sample's Inventory does; a delayed command is fine, since it continues the tenant of the Operation that scheduled it. A Snapshot of an aggregate of the Module's own DbContext whose mapped key neither includes its `Tenant` nor is a single Guid (`SnapshotKeyTenancyCheck`, with `UsePouryaKitWolverine(connectionName)`): the Snapshot's identity and version key hold the aggregate's key and no tenant (ADR 0006's #117 amendment), so key such an aggregate by a Guid version 7, as Catalog's products are, or by its tenant and its own code.

## A tenant-scoped aggregate

```csharp
internal sealed class Product : AggregateRoot<ProductId>, IAuditable, ISoftDeletable, ITenantScoped
{
    public string Tenant { get; private set; } = "";

    public static Result<Product> Create(ProductId id, CategoryId categoryId, string name, string tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        // ...
        var product = new Product { Id = id, Tenant = tenant, CategoryId = categoryId, Name = name.Trim() };
        // ...
    }
}

// Its handler takes the tenant from the Operation Context. Catalog is tenant-scoped, so it always has one.
internal sealed class CreateProductCommandHandler(IRepository<Product, ProductId> products, OperationContext context)
    : CommandHandler<CreateProductCommand, Guid>
{
    // ... Product.Create(new ProductId(command.ProductId), new CategoryId(command.CategoryId), command.Name, context.Tenant!)
}
```

- The aggregate sets its own `Tenant`, from the value its handler takes from `OperationContext.Tenant`; the Kit never sets it. Declare it with a private setter, as `IAuditable`'s properties are. Its column is `tenant`, snake-cased like every other.
- Implement `ITenantScoped` on an aggregate or entity that is not mapped as an owned type, at the root of its inheritance hierarchy or below a root that implements it too, as `ISoftDeletable` is: EF Core filters only those, so the model refuses to build otherwise, naming the type.
- A unique value is unique per tenant: Catalog's name index is on `(tenant, name)`, so two tenants each have a product of one name, and one tenant's name tells another nothing.
- Make every aggregate of a tenant-scoped Module that holds a tenant's data tenant-scoped. Catalog's `Category` is, because it counts the tenant's products.

## Reads

- Every EF Core query of an `ITenantScoped` entity returns only the rows of the tenant of the Operation running it, through a named query filter, `PouryaKitDbContext.TenantFilter` (`PouryaKit.Tenant`), beside `SoftDeleteFilter`. The repository's `FindAsync`, `ListAsync`, `AnyAsync` and `CountAsync` and a specific repository's projection all read through it, so another tenant's aggregate is not found.
- The filter reads the tenant when each query runs, from the scope's Operation, never when the model is built or the DbContext created: the migration tool and the composition checks build a DbContext where no Operation began, and a DbContext made before its scope's Operation began reads that Operation's tenant. Where the Operation has no tenant, or none began, the filter reads no rows.
- A read that must cross tenants sets the filter aside on purpose, in an EF Core query over the DbContext: `IgnoreQueryFilters([PouryaKitDbContext.TenantFilter])`. The repository's members never do. Only that filter is set aside: a removed row stays out unless the read names `SoftDeleteFilter` too.
- Dapper runs exactly the SQL you write, so a plain SQL read of a tenant-scoped table states `tenant = @Tenant` itself, with the tenant from the injected `OperationContext`, as Catalog's `GetProduct` and `ListProducts` do, in the one shape a structure test holds every such read to: the predicate a top-level conjunct of the `WHERE` of one `SELECT`, for each tenant-scoped table it reads ([dapper-reads.md](dapper-reads.md#reading)).

## Saves

- The Unit of Work's prepare step refuses the save with `TenantErrors.Mismatch` (`Tenant.Mismatch`, kind `Forbidden`) and rolls the Operation back when an `ITenantScoped` aggregate it added belongs to another tenant than the Operation's, or one it changed or removed was loaded under another tenant, or had its tenant changed. It judges every aggregate the step attributes a change to: one whose part changed, both holders of a part moved between aggregates, and the parts a removal deletes with it. A removed soft-deletable aggregate is judged by the tenant it was loaded under, before it becomes a mark.
- So a read that sets `TenantFilter` aside may read another tenant's rows, but an Operation can never save a change to them: it answers 403 over HTTP. It holds wherever an `ITenantScoped` aggregate is saved, as the filter does, in a Module that is tenant-scoped or not, and an Operation without a tenant saves none.
- What it does not see: an entity attached with `Attach` and a tenant the code invents is judged by that tenant; load aggregates through a query or the repository.

## Deliveries

- A Notification, Integration Event or Snapshot delivered into a tenant-scoped Module runs under the tenant it carries, the one the publishing Operation had, so its handler writes for that tenant: Inventory, which is not tenant-scoped, keys its copy of Catalog's products by `(tenant, product_id)`, from the injected `OperationContext`.
- One that carries no tenant is refused before its Receipt and before its handler, whatever the handler writes with, plain SQL included: it fails with `Tenant.Required`, is retried, then set aside as a dead letter whose message names it. The mistake is the publisher's, an Operation with no tenant whose work reaches a tenant-scoped Module; a replay is refused the same way.

## What the Kit guarantees

- In a tenant-scoped Module, no command or query runs without a tenant, a trusted continuation included; it fails with `Tenant.Required` before its validator and handler.
- An EF Core read of a tenant-scoped entity returns only the Operation's tenant's rows; a cross-tenant read names `TenantFilter`.
- An application without Modules that declares it on its own services gets the same requirement for the commands and queries of the assemblies it names.
- No Operation saves a change to another tenant's aggregate, or moves one between tenants; it fails with `Tenant.Mismatch` and nothing is saved.
- A tenant-scoped Module applies no delivery without a tenant; a delivery with one runs under it.
- A tenant-scoped Module composes no recurring command and no Snapshot whose key two tenants could share.

## Limits

- An anonymous caller has no tenant, so no endpoint of a tenant-scoped Module serves one: Catalog's `ListProducts` is authenticated, like every Catalog endpoint. A tenant comes only from a verified claim; a tenant taken from the host name or the route is not supported.
- A plain SQL write is the Feature's too: no save check sees it.
- A row written before its table kept the tenant cannot learn it later: a Module's own rows from before it was tenant-scoped have no tenant to recover, and a copy of another Module's data cannot be replayed, since the Snapshot versions already applied stop it. The migration that adds the tenant refuses a table that holds such a row rather than guess its tenant, as Catalog's `Tenants` and Inventory's `CatalogProductTenants` migrations do; an empty tenant would hide the row from every Operation and fail any change to it. The Kit has no backfill.
- Row-level security in PostgreSQL is not used (spec #115, owner decision T2), and the Receipt and Snapshot version keys hold no tenant (T1).

## Testing

- Prove isolation with two tenants at the Gateway: create in tenant A and in tenant B with `new OperationContext(actor, "a")`, then show that A's get, list, change and removal of B's aggregate find nothing (`TenancyTests` in the Proving Sample's tests).
- Over HTTP the tenant is the authenticated caller's claim: the test scheme issues it from its tenant header, `AppFixture.Client(caller, tenant, permissions)`.
- `GatewayTestExtensions`' calls without a context run with no tenant, so a tenant-scoped Module refuses them; pass a context with a tenant. The Proving Sample's tests add `CatalogGatewayExtensions`, the same calls for Catalog in a test tenant.
- Show the requirement with a context without a tenant, and for a continuation with a context continued inside an Operation of a Module that is not tenant-scoped.
- Show the save check with two tenants: load the other tenant's aggregate with `TenantFilter` set aside in a handler, change its root, a part, move a part in or out, remove it, and expect `TenantErrors.Mismatch` with its rows unchanged; make the same change to the Operation's own tenant's to show it saves (`TenantOwnershipTests`).
- Show a delivery refusal by sending to the subscription's queue with the Kit's headers and no tenant, then reading the dead letter (`TenantDeliveryTests`).
