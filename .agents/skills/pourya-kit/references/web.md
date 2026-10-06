# Web

`PouryaKit.Web`: an endpoint beside each Feature, a Result as an HTTP response, one problem response for every failure, and the Operation Context built from the authenticated caller. Namespace `PouryaKit.Web`. It references `PouryaKit.Cqrs` and the ASP.NET Core shared framework only; a Module project that declares endpoints references it, and it brings ASP.NET Core with it.

- [The Host](#the-host)
- [Endpoints](#endpoints)
- [From Result to response](#from-result-to-response)
- [Problem responses](#problem-responses)
- [The caller and the Actor](#the-caller-and-the-actor)
- [Testing over HTTP](#testing-over-http)

The Proving Sample's `CreateProduct` endpoint, beside its command in `Products/Features/CreateProduct/`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

internal sealed class CreateProductEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPost(
            "/products",
            async (
                CreateProductBody body,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) =>
                (
                    await catalog.SendAsync(
                        new CreateProductCommand(body.ProductId, body.CategoryId, body.Name),
                        context,
                        cancellationToken
                    )
                ).ToHttpResult(id => TypedResults.Created($"/catalog/products/{id}", new { id }))
        );
}

internal sealed record CreateProductBody(Guid ProductId, Guid CategoryId, string Name);

// In the Composition Root: module.AddPouryaKitEndpoints("/catalog", typeof(CatalogModule).Assembly);
```

## The Host

The Proving Sample's Host composition, one class shared by `Program` and the tests:

```csharp
using Billing;
using Catalog;
using Catalog.Products.Events.Notifications;
using Inventory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Messaging.Wolverine.EFCore;
using PouryaKit.Modularity;
using PouryaKit.Web;

public static class ModularMonolithHost
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The application authenticates its callers itself; JwtBearer reads Authentication:Schemes:Bearer.
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddAuthorization();
        builder.Services.AddPouryaKitWeb();
        builder.AddPouryaKitModules(modules =>
        {
            modules.Add<CatalogModule>();
            modules.Add<InventoryModule>();
            modules.Add<BillingModule>();
        });
        builder.UsePouryaKitWolverine(connectionName: "app");   // the Modules' deliveries (messaging.md)
        builder.Services.AddSingleton<INewProductAnnouncer, LoggedNewProductAnnouncer>();
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.UseExceptionHandler();   // first, so an exception in routing or authentication still answers a problem response
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPouryaKitModuleEndpoints();
        return app;
    }
}

// Program.cs: await ModularMonolithHost.Build(ModularMonolithHost.CreateBuilder(args)).RunAsync();
```

- `builder.Services.AddPouryaKitWeb()` registers what every endpoint answers with: problem responses, the handler that turns an exception into one, the same answer to an unreadable request in every environment, a problem response for every failure answered with an empty body ([Problem responses](#problem-responses)), typed IDs written as their bare values in JSON, and `PouryaKitWebOptions`.
- The application configures its own authentication and authorization; the Kit chooses no identity provider and issues no token.
- `app.UseExceptionHandler()` comes first in the pipeline, and `UseRouting()`, `UseAuthentication()` and `UseAuthorization()` follow it, each called explicitly. Left out, `WebApplication` inserts those three ahead of everything the application adds, and an exception thrown in authentication escapes the handler.
- `app.MapPouryaKitModuleEndpoints()` maps every Module's endpoints, each Module under its own route group.

## Endpoints

- Write one `internal sealed` class implementing `IEndpoint` beside its Feature, named after it with the `Endpoint` suffix, with a parameterless constructor. Its `Map(routes)` maps the Feature's route with a minimal API (`MapGet`, `MapPost`, ...) on its Module's route group, so the path is relative to the group's prefix.
- `module.AddPouryaKitEndpoints(routePrefix, assembly)` in the Module's Composition Root finds every `IEndpoint` in the assembly, internal ones included, and maps them under `routePrefix` (`/catalog`). There is no central route list. It throws when the assembly has no endpoint. An application without Modules calls it on its own services.
- The endpoint injects `OperationContext`, its Module's `IGatewayProcessor<TModule>` and the `CancellationToken`, builds the command or query from the route and the body, sends it through the Gateway with that context, and returns `ToHttpResult()`. It holds no business logic and touches no repository or DbContext.
- Declare a request body as an `internal sealed record` beside the endpoint (`CreateProductBody`) and map it to the command by hand.
- Leave a route parameter unconstrained (`/products/{productId}`, not `{productId:guid}`): an id that does not parse is the caller's mistake and answers 400, where a constraint would answer 404. Check input in the command's or query's validator, which answers 400 with every failure.

The `GetProduct` and `ListProducts` endpoints, in their own Feature folders:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Web;

internal sealed class GetProductEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapGet(
            "/products/{productId}",
            async (
                Guid productId,
                OperationContext context,
                IGatewayProcessor<CatalogModule> catalog,
                CancellationToken cancellationToken
            ) => (await catalog.QueryAsync(new GetProductQuery(productId), context, cancellationToken)).ToHttpResult()
        );
}

// The product list is the storefront's: anyone may read it, and an anonymous visitor's Operation runs as the storefront.
internal sealed class ListProductsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes
            .MapGet(
                "/products",
                async (OperationContext context, IGatewayProcessor<CatalogModule> catalog, CancellationToken cancellationToken) =>
                    (await catalog.QueryAsync(new ListProductsQuery(), context, cancellationToken)).ToHttpResult()
            )
            .AllowAnonymousAs(Actor.System("catalog.storefront"));
}
```

## From Result to response

- `ToHttpResult()` is the one place a Result becomes a response. On a `Result` without a value, success answers 204 No Content. On a `Result<T>`, success answers 200 OK with the value as the JSON body.
- `ToHttpResult(value => ...)` answers another success shape, such as 201 Created with a location: `.ToHttpResult(id => TypedResults.Created($"/catalog/products/{id}", new { id }))`.
- A failure answers a problem response, whatever overload the endpoint used.

## Problem responses

- Every failure answers an RFC 9457 problem response, written as `application/problem+json` even when the caller's `Accept` asks for something else. It carries ASP.NET Core's `type`, `title` and `status`, and, for a failed Result, an `errors` array listing every Error in order with its `code`, its `message`, and its `target` when a validation Error names one.
- The status comes from the Errors' kinds: `Validation` 400, `NotFound` 404, `Conflict` 409, `Forbidden` 403, `Unexpected` 500. When the Errors are of several kinds, the most severe decides, in the order unexpected, forbidden, not found, conflict, validation: the caller cannot succeed by correcting its input while a more severe reason stands. The `errors` array still lists every Error.
- Validation failures from the pipeline answer 400 with each failure's code and target. The Unit of Work's Errors answer by their kinds as well: a concurrent change (`UnitOfWork.ConcurrencyConflict`) 409, and a unique index the Module mapped the status of its Error, 409 for `Product.NameInUse` ([persistence.md](persistence.md#unique-values)).
- An exception, from a handler, the database, authentication or an endpoint, answers 500 with no message, type or stack in the body. The Kit logs it at Error level.
- A request ASP.NET Core cannot read, such as malformed JSON or a route value that does not parse, answers 400 as a problem response in every environment, with no `detail` and no `errors`, and is logged only at Debug level.
- A failure that ASP.NET Core answers itself with an empty body, before any endpoint or exception handler runs, is written as the same problem response, with its status and no `errors`: routing's 405 for a method the route does not serve and 415 for a body of a content type the endpoint does not accept, and the 401, 403 or 404 that authentication, authorization or routing answers. `AddPouryaKitWeb` adds this through a startup filter, outermost in the pipeline, so the Host adds nothing for it. A response that already has a body is left as it is.

## The caller and the Actor

- Every Module's route group requires an authenticated caller. A request without one is refused by the application's authentication scheme before the endpoint runs (401 with JwtBearer, written as a problem response).
- Before the endpoint binds its parameters, the Kit begins the request's Operation with an `OperationContext` built from the caller: `Actor.Person(id)` from the Actor claim, the tenant from the tenant claim when one is configured, and a new chain of work (correlated with its own `OperationId`, no causation; no incoming header sets them). The endpoint injects that context and passes it to the Gateway; it never builds one itself.
- `PouryaKitWebOptions` names the claims: `ActorClaim`, by default `ClaimTypes.NameIdentifier`, which JwtBearer maps a token's `sub` to (set `"sub"` when the scheme keeps the token's claim names), and `TenantClaim`, null by default for an application without tenants. Set them with `AddPouryaKitWeb(web => web.TenantClaim = "tenant")` or `Configure<PouryaKitWebOptions>`.
- A caller whose identity lacks the tenant claim runs with a null tenant and is not refused. An authenticated caller without the Actor claim is a configuration bug: the request answers 500 and the log names the missing claim.
- A public endpoint states the Actor its anonymous callers run as with `.AllowAnonymousAs(Actor.System("catalog.storefront"))`, as `ListProducts` does; an authenticated caller of it still runs as themselves. A plain `.AllowAnonymous()` on a Module's endpoint fails the Host when it starts, because no Operation runs as an unnamed Actor.
- A rule for one command or query beyond "authenticated" is an `IAuthorizer` ([commands-queries-validation.md](commands-queries-validation.md#authorization-hook)), which answers 403 with its `Forbidden` Error.

## Testing over HTTP

The test project references `Microsoft.AspNetCore.TestHost`. Its fixture builds the Host with the same `CreateBuilder` and `Build` as `Program`, serves it in memory with `builder.WebHost.UseTestServer()`, and makes a test authentication scheme the default ([testing.md](testing.md)). The Proving Sample's scheme reads the caller from the `Authorization` header and issues the claim the Kit reads:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// "Authorization: Test <id>" is the authenticated person <id>; no header is an anonymous caller.
internal sealed class TestAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder
) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith($"{SchemeName} ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, header[(SchemeName.Length + 1)..])], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
```

A test sends real requests through the whole pipeline and asserts the status, the problem response and the rows:

```csharp
[Fact]
public async Task Creating_a_product_with_a_name_in_use_answers_409_with_its_error_and_writes_nothing()
{
    using var client = Fixture.Host.GetTestClient();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(TestAuthentication.SchemeName, "ada");
    var category = Guid.CreateVersion7();
    await client.PostAsJsonAsync("/catalog/categories", new { categoryId = category, name = "Lamps" }, TestContext.Current.CancellationToken);
    await client.PostAsJsonAsync(
        "/catalog/products",
        new { productId = Guid.CreateVersion7(), categoryId = category, name = "Desk lamp" },
        TestContext.Current.CancellationToken
    );

    var response = await client.PostAsJsonAsync(
        "/catalog/products",
        new { productId = Guid.CreateVersion7(), categoryId = category, name = "Desk lamp" },
        TestContext.Current.CancellationToken
    );

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    Assert.Equal(
        """[{"code":"Product.NameInUse","message":"Another product already has this name."}]""",
        problem.RootElement.GetProperty("errors").GetRawText()
    );
}
```

- `Fixture.Host.GetTestClient()` (`Microsoft.AspNetCore.TestHost`) returns an `HttpClient` over the in-memory server; a client without the header is an anonymous caller.
- A second `AddAuthentication(scheme)` call after the Host's makes the test scheme the default, so the Host's own scheme needs no configuration in tests. Issue the claim the Host's `ActorClaim` names, and a tenant claim when `TenantClaim` is set.
- A test that needs another composition, such as another environment or an endpoint the shared Host must not map, builds and starts a Host of its own from the same composition methods.
