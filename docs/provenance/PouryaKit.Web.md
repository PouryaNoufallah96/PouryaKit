# PouryaKit.Web provenance

Adopted implementations in `PouryaKit.Web`. Format and rules: [README](README.md).

## Package dependency

None. `PouryaKit.Web` references `PouryaKit.Cqrs` and the ASP.NET Core shared framework (`FrameworkReference Microsoft.AspNetCore.App`), as plan section C lists; it takes no NuGet package and does not reference `PouryaKit.Modularity`.

## Endpoint convention

- **Kit files:** `src/PouryaKit.Web/IEndpoint.cs`, `src/PouryaKit.Web/WebServiceCollectionExtensions.cs`, `src/PouryaKit.Web/WebEndpointRouteBuilderExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Web/Endpoints/IEndpointDefinition.cs`, `src/BuildingBlocks/BuildingBlocks.Web/Extensions/EndpointExtensions.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #14
- **Intentional differences:**
  - Endpoints are found in the assembly a Module's Composition Root names, never in every loaded assembly, and each Module's are mapped under its own route group. The Composition Root declares them with `AddPouryaKitEndpoints(routePrefix, assembly)` on its builder, an `IServiceCollection`, so Web needs nothing from Modularity; an application without Modules calls it on its own services.
  - Every Module's route group requires an authenticated caller. A public endpoint states it with `AllowAnonymousAs(actor)`, naming the Actor an anonymous caller's Operation runs as; plain `AllowAnonymous` on a Module's endpoint fails when the endpoint is built.
  - The request's Operation is begun with an `OperationContext` built from the authenticated `ClaimsPrincipal` before the endpoint binds its parameters, so an endpoint injects the context and passes it to its Module's Gateway (ADR 0007).
  - The OpenAPI response helpers of `EndpointExtensions.cs` are left out (Wave 2, `PouryaKit.Web.OpenApi`).

## Result to HTTP

- **Kit files:** `src/PouryaKit.Web/ResultHttpExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Web/Endpoints/EndpointResponseHandler.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #14
- **Intentional differences:**
  - Extension members on `Result` and `Result<T>` instead of a base class every endpoint derives from.
  - A failure lists every Error in the problem response's `errors` member (code, message, and the target of a validation Error); upstream answers with the first Error only.
  - The status comes from the Errors' `ErrorKind`, which keeps HTTP out of the domain; upstream stores the HTTP status in the Error's code. With several kinds, the most severe decides.

## Problem responses for unexpected exceptions

- **Kit files:** `src/PouryaKit.Web/UnexpectedExceptionHandler.cs`, `src/PouryaKit.Web/WebServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/mehdihadeli/vertical-slice-api-template
- **Commit:** 4a398078850cc6bd10f521a09e92c4983cc44955
- **Upstream paths:** `src/Shared/Web/ProblemDetail/ProblemDetailsService.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #14
- **Intentional differences:**
  - An `IExceptionHandler` writes through ASP.NET Core's own `ProblemHttpResult`, as a failed Result is written, instead of replacing `IProblemDetailsService`, so failed Results and exceptions share one problem envelope; it falls back to `application/problem+json` when the caller's `Accept` rules out every writer.
  - The response never carries the exception's message, type or stack; upstream copies the message into `detail` and the type name into `title`. Every exception answers 500, except a request ASP.NET Core could not read (`BadHttpRequestException`), which answers its own status: an expected failure is a Result, not an exception (G.1), so there are no exception-to-status mappers. Binding failures throw in every environment, so they answer the same problem response everywhere.
  - The handler logs an unexpected exception at Error as the HTTP-level record of the failed request; the dispatcher logs one a handler throws as well, but an exception raised outside an Operation has only this entry, and the .NET 10 exception handler middleware no longer logs one a handler handled.
