# Modules

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
        module.AddScoped<IProductRepository, ProductRepository>();   // see persistence.md, The repository
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

## The Composition Root

- Implement `IModuleDefinition` once per Module, as a public sealed class with a parameterless constructor. Everything else in the Module stays `internal`.
- `Configure(ModuleBuilder module)` declares everything the Module needs: its data (`UsePostgres`), its Features (`AddPouryaKitCqrs`, `AddPouryaKitValidation`) and its own services. `ModuleBuilder` is an `IServiceCollection` over the Host's one container, so every registration extension works on it.
- The Unit of Work registered on a Module's builder becomes that Module's own; every other registration goes into the Host's container as it is.
- The Host adds each Module once: `builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>().Add<BillingModule>())`. That runs each Composition Root and registers the Module's Gateway.

## The Gateway

- `IGatewayProcessor<TModule>` is how anything outside a Module runs one of its Operations: `SendAsync(command)` for a command, `QueryAsync(query)` for a query, each returning the Result. Resolve it from the Host's services.
- Each call is one Operation in a new scope, tagged with the Module and disposed when the call returns. A command reaches its Module's Unit of Work only through that tag: sent through `IDispatcher` from a scope no Gateway opened, it throws `InvalidOperationException`.
- Commands and queries stay `internal`, so a caller that builds one lives in the Module's assembly or is granted `InternalsVisibleTo`, as the Proving Sample's test project is.

```csharp
var gateway = host.Services.GetRequiredService<IGatewayProcessor<CatalogModule>>();
var lamps = await gateway.SendAsync(new CreateCategoryCommand(Guid.CreateVersion7(), "Lamps"), ct);
var created = await gateway.SendAsync(new CreateProductCommand(Guid.CreateVersion7(), lamps.Value, "Desk lamp"), ct);
```

*Not yet available* (#13): the Operation Context, startup validation of each Module, and synchronous questions one Module asks another.
