# PouryaKit.Web provenance

Adopted implementations in `PouryaKit.Web`. Format and rules: [README](README.md).

The status code pages that answer a failure ASP.NET Core leaves with an empty body, such as routing's 405 and 415, as a problem response (`ProblemStatusCodePages`, ticket #19) are new Kit code over ASP.NET Core's own `UseStatusCodePages`, so they have no record here. The outgoing resilience registration (`AddPouryaKitResilience`, `HttpReplay`), the context header rule and the trusted-peer continuation (`PouryaKitTrustedPeerOptions`, `OperationContextHttpHeaders`, ticket #48) are new Kit code over the standard resilience handler and ASP.NET Core's own authentication, so they have no record either.

## Package dependencies

Checked on nuget.org on 2026-10-07 (ticket #48); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Microsoft.Extensions.Http.Resilience` 10.10.0, `MIT`. The standard .NET HTTP resilience handler behind `AddPouryaKitResilience`, used without Aspire or ServiceDefaults. Its restored closure adds eight `Microsoft.Extensions.*` 10.10.0 packages (`Resilience`, `Http.Diagnostics`, `Telemetry`, `Telemetry.Abstractions`, `Compliance.Abstractions`, `AmbientMetadata.Application`, `DependencyInjection.AutoActivation`, `Diagnostics.ExceptionSummarization`), all `MIT`, and `Polly.Core`, `Polly.Extensions` and `Polly.RateLimiting` 8.4.2, `BSD-3-Clause`; `LicenceTests` checks the whole closure, and `PackageGraphTests` keeps every Aspire package out of it.

`PouryaKit.Web` also references `PouryaKit.Cqrs`, `PouryaKit.Core` and the ASP.NET Core shared framework (`FrameworkReference Microsoft.AspNetCore.App`), as plan section C lists, and does not reference `PouryaKit.Modularity`. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

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

## Authenticated access by default

- **Kit files:** `src/PouryaKit.Web/WebServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Security/Jwt/Extensions/AuthExtensions.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #38
- **Intentional differences:**
  - The Kit sets the `FallbackPolicy`, not the `DefaultPolicy` (upstream `AuthExtensions.cs:53-59`), so an endpoint that states nothing at all, outside every Module's route group too, requires an authenticated caller; it keeps a fallback policy the application set itself.
  - The policy names no authentication scheme, so it uses the application's default one; Web chooses no identity provider (plan section C).
  - No claim or role policies are registered, and nothing adds a policy while the Host runs (plan I.3 rejects `FeaturePolicyProvider.cs:20-24`); a permission is checked on the dispatcher's authorization hook by `PouryaKit.Security` instead.
  - It lives in `PouryaKit.Web`, which owns the HTTP boundary, rather than in `PouryaKit.Security` as plan I.3 places it, so Security needs no ASP.NET Core framework reference (ticket #38).

## Outgoing Operation Context handler

- **Kit files:** `src/PouryaKit.Web/OperationContextHttpHandler.cs`, `src/PouryaKit.Web/WebHttpClientBuilderExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Security/Utilities/HttpClientAuthorizationDelegatingHandler.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #48
- **Intentional differences:**
  - Kept: a `DelegatingHandler` added to a client through `HttpClientFactory` writes the calling side's identity onto every outgoing request.
  - It writes the Operation Context as the Kit's `pouryakit-*` headers (Messaging's names), never the incoming `Authorization` header: upstream copies the caller's bearer token (`:23-30`), which plan I.3 and G.6 reject.
  - It reads no `IHttpContextAccessor`: the context travels on the request (`WithOperationContext`), because the factory builds handlers in a scope of its own and ADR 0007 rejects ambient reads; so a delivery or a job can call out too. A request without a context throws before it is sent.
  - Every value is checked against the Kit's header rule before any header is written, so a claims-derived value cannot add a header line.
