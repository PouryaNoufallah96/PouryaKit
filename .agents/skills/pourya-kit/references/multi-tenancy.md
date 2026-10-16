# Multi-tenancy

Multi-tenancy is a registration across packages the Host already has, not a package: `ITenantScoped` (`PouryaKit.Domain`) marks an aggregate as one tenant's, `PouryaKitDbContext` (`PouryaKit.Persistence.EFCore`) reads it only for the Operation's tenant, and `AddPouryaKitTenancy` (`PouryaKit.Cqrs`) makes a Module, or an application without Modules, tenant-scoped, so that every command and query of it needs a tenant. The tenant is `OperationContext.Tenant`, which the HTTP boundary takes only from a verified claim ([security.md](security.md#jwt-bearer-authentication), [web.md](web.md#the-caller-and-the-actor)).

- [Declaring a tenant-scoped Module](#declaring-a-tenant-scoped-module)
- [A tenant-scoped aggregate](#a-tenant-scoped-aggregate)
- [Reads](#reads)
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

- `AddPouryaKitTenancy()` registers the tenant requirement, an authorizer on the pipeline's authorization hook, for every concrete command and query of the assembly of the Module's definition, internal ones included. An Operation whose context has no tenant, or a scope nobody began, fails with `TenantErrors.Required` (`Tenant.Required`, kind `Forbidden`) before its validator and handler run; `ToHttpResult()` answers it with 403 and the Error in the problem response's `errors`.
- It runs for a trusted continuation of one of those commands and queries too, unlike a permission check: a delayed command's run, a trusted peer's call, a command a Notification or Integration Event Handler sends during its delivery, or `Continue()` inside the asking Operation, as `ICatalogProducts` asks Catalog from another Module. Such a continuation runs with the tenant it carries, or is refused (spec #115; the one exception to G.6's "continuations are not authorized again", recorded in ADR 0007's #116 amendment). A delivery itself runs the Kit's own command, which is in no Feature assembly, so the requirement does not check it ([Limits](#limits)).
- An Operation's authorizers run in the order registered, so declare it before `AddPouryaKitCqrs` and `AddPouryaKitPermissions`: it then refuses a tenantless Operation before any authorizer of the Module's own reads data.
- An application without Modules declares it on its own services and names its Feature assemblies, as it does for `AddPouryaKitCqrs`: `services.AddPouryaKitTenancy(typeof(Program).Assembly)`. With no assembly there it throws `ArgumentException`, since it would cover nothing.
- The declaration is recorded for the Host: `HostComposition.IsTenantScoped(module)`, null for the application's own services, tells a Kit registration or composition check which Modules are tenant-scoped.

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
- Dapper runs exactly the SQL you write, so a plain SQL read of a tenant-scoped table states `tenant = @Tenant` itself, with the tenant from the injected `OperationContext`, as Catalog's `GetProduct` and `ListProducts` do ([dapper-reads.md](dapper-reads.md#reading)).

## What the Kit guarantees

- In a tenant-scoped Module, no command or query runs without a tenant, a trusted continuation included; it fails with `Tenant.Required` before its validator and handler.
- An EF Core read of a tenant-scoped entity returns only the Operation's tenant's rows; a cross-tenant read names `TenantFilter`.
- An application without Modules that declares it on its own services gets the same requirement for the commands and queries of the assemblies it names.

## Limits

- An anonymous caller has no tenant, so no endpoint of a tenant-scoped Module serves one: Catalog's `ListProducts` is authenticated, like every Catalog endpoint. A tenant comes only from a verified claim; a tenant taken from the host name or the route is not supported.
- Not yet built (Wave 3, ticket #117): the save-time check that refuses an aggregate of another tenant, the refusal of a delivery with no tenant into a tenant-scoped Module before its Receipt, the composition refusals of a recurring command and of a shared Snapshot key in such a Module. Ticket #118 adds the structure rule that holds every Dapper read of a tenant-scoped table to the tenant predicate; until then the predicate is the Feature's to write.
- Row-level security in PostgreSQL is not used (spec #115, owner decision T2), and the Receipt and Snapshot version keys hold no tenant (T1).

## Testing

- Prove isolation with two tenants at the Gateway: create in tenant A and in tenant B with `new OperationContext(actor, "a")`, then show that A's get, list, change and removal of B's aggregate find nothing (`TenancyTests` in the Proving Sample's tests).
- Over HTTP the tenant is the authenticated caller's claim: the test scheme issues it from its tenant header, `AppFixture.Client(caller, tenant, permissions)`.
- `GatewayTestExtensions`' calls without a context run with no tenant, so a tenant-scoped Module refuses them; pass a context with a tenant. The Proving Sample's tests add `CatalogGatewayExtensions`, the same calls for Catalog in a test tenant.
- Show the requirement with a context without a tenant, and for a continuation with a context continued inside an Operation of a Module that is not tenant-scoped.
