---
name: openapi-versioning-facts
description: Ticket #41 facts - Asp.Versioning.OpenApi 10.2 wiring, clock-reading default notices, URL vs header readers need two groups, startup failures without registration, Scalar under the fallback policy, a Web-only test Host
metadata:
  type: project
---

Verified in ticket #41 (2026-10-07), Asp.Versioning 10.2.3, Microsoft.AspNetCore.OpenApi 10.0.12, Scalar.AspNetCore 2.17.14:

- **Wiring:** `AddApiVersioning(...).AddApiExplorer(...).AddOpenApi(o => ...)` (the versioning builder's `AddOpenApi`) plus `MapOpenApi().WithDocumentPerVersion()` serves `/openapi/v1.json` per described version; no `services.AddOpenApi()` needed. Per-document transformers go on `o.Document` (the `OpenApiOptions`), notices on `o.DocumentDescription`. Read the library with `ilspycmd -p -o <dir> ~/.nuget/packages/asp.versioning.openapi/10.2.3/lib/net10.0/Asp.Versioning.OpenApi.dll`.
- **Default notices read the clock:** `OpenApiDocumentDescriptionOptions.SunsetNotice`/`DeprecationNotice` compare the date with `DateTimeOffset.Now` ("was"/"will be"). Replace both for ADR 0005; the description is then exact text a test can pin.
- **Sunset policies are per version in documents:** `GroupedApiVersionDescriptionProvider` resolves `TryGetPolicy(version)` only, so a document's sunset is Host-wide per version; response `Sunset`/`Link` headers come from the same policy.
- **A `v{version:apiVersion}` route can never be selected by header:** a header with a different version answers 400 ambiguous, none answers by URL. Header selection needs an unversioned group bound to the same version set; the Kit maps both inside the Module group. Consequence: each endpoint is mapped twice, so `WithName` would duplicate.
- **Without registration it fails at build, loudly:** `MapToApiVersion` on a group without a version set throws "does not have an associated API version set"; `WithApiVersionSet` without `AddApiVersioning` throws "No service for type IApiVersionParameterSource". The Kit's seam throws its own message first, during `MapPouryaKitModuleEndpoints` (i.e. in the test's Host-building call, not `StartAsync`).
- Unversioned endpoints (Stub) appear in every version's document. Asp.Versioning's empty 400/404 bodies become problem responses through Web's `ProblemStatusCodePages`.
- **Scalar:** only its static assets are `AllowAnonymous`; the page and `/openapi/*.json` hit the fallback policy (401) unless the Host allows anonymous. `/scalar` 302s to `/scalar/`; the page embeds `"sources":[{"title":"v1","url":"openapi/v1.json"},...]`.
- **A Web-only test Host** needs an assembly whose endpoints need no Module: the Stub's handlers need Catalog's `ICatalogProducts` and its endpoints Gateways. `tests/ModularMonolith.Tests/Unversioned/` is that assembly (pattern of `Misnamed/`: own csproj, `Compile Remove`, slnx row); `AddPouryaKitCqrs()` with no assembly gives `OperationContext`.
- The shared fixture clients send `api-version: 2.0` (`AppFixture.ApiVersion`), so other tests keep `/catalog/...` paths; versioning tests remove the header.
- **The scratchpad root is shared** with parallel implementers (`mutate.py`, `edit_*.py` of other tickets sit there): keep a ticket's scripts in a subfolder.
- **Two tickets each adding a package:** both moved counts 15→16, so the rebase auto-merged `Assert.Equal(16, …)` and "Sixteen" doc lines without conflict; resolve conflicts with a script that re-applies the edit to the tip's text, then grep every count word.

**Why:** each decided the #41 design or cost a round.
**How to apply:** for #53 (templates that compose Web.OpenApi), any ticket touching Module route groups, OpenAPI documents or a package count. Related: [[web-aspnetcore-facts]], [[security-permission-facts]], [[core-helpers-facts]].
