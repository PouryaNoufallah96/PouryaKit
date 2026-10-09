# PouryaKit.Web.OpenApi provenance

Adopted implementations in `PouryaKit.Web.OpenApi`. Format and rules: [README](README.md).

The per-operation security requirement (`BearerSecurity`), the configured sunsets (`PouryaKitOpenApiOptions`, `ApiVersionSunset`), the clock-free document notices and the Module route groups bound to a version set (`AddPouryaKitApiVersions`, `VersionedModuleRouteGroups`, Web's `IModuleRouteGroups`) are new Kit code over Asp.Versioning, ASP.NET Core's OpenAPI and Scalar, so they have no record here. Plan I.3 also names Cybernod's `OpenApiVersioningDocumentTransformer.cs` and CloudNativeKit's `MinimalApiExtensions.cs`; neither was taken: `Asp.Versioning.OpenApi` writes each version's deprecation and sunset into its document itself, and the Kit binds the version set to the Module's existing route group instead of a group per version.

## Package dependencies

Checked on nuget.org on 2026-10-07 (ticket #41), from each package's nuspec in the flat container: each is the latest stable version, has a `net10.0` asset, and is `MIT`. None is reciprocal or commercial (ADR 0002).

- `Asp.Versioning.Http` 10.2.3, `MIT`. API versions on minimal APIs: version sets, the URL segment and header readers, sunset policies and the reported-versions headers. It brings `Asp.Versioning.Abstractions` 10.2.1, `MIT`.
- `Asp.Versioning.OpenApi` 10.2.3, `MIT`. One ASP.NET Core OpenAPI document per API version (`AddOpenApi` on the versioning builder, `WithDocumentPerVersion`), with each version's deprecation and sunset in its document. It brings `Asp.Versioning.Mvc.ApiExplorer` and `Asp.Versioning.Mvc` 10.2.1, `MIT`, the API explorer that describes the versions (`IApiVersionDescriptionProvider`).
- `Microsoft.AspNetCore.OpenApi` 10.0.12, `MIT`. ASP.NET Core's own OpenAPI document generation and its transformers. It brings `Microsoft.OpenApi` 2.x, `MIT`.
- `Scalar.AspNetCore` 2.17.14, `MIT`. The Scalar API reference over the documents; no further package.

`LicenceTests` checks the whole restored closure. No Swagger generator, Swashbuckle, NSwag or AsyncAPI package is referenced; `PackageGraphTests` keeps Swashbuckle and NSwag out of the closure, and API versioning, OpenAPI and Scalar out of `PouryaKit.Web`'s. `PouryaKit.Web.OpenApi` also references `PouryaKit.Web` and the ASP.NET Core shared framework (`FrameworkReference Microsoft.AspNetCore.App`), as plan section C lists since ticket #41. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## API version readers

- **Kit files:** `src/PouryaKit.Web.OpenApi/WebOpenApiServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/meysamhadeli/woo
- **Commit:** 149a3cf168496c3bec85945f83ad222e43720e4d
- **Upstream paths:** `src/Web/ApiVersioningExtensions.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #41
- **Intentional differences:**
  - Kept: the URL segment and `api-version` header readers combined, reported versions, group names `'v'VVV` and the version substituted in the URL (`:19-49`).
  - No `AssumeDefaultVersionWhenUnspecified` and no default version: a request on an unversioned path names its version in the header or is refused 400, so no caller is bound to a version it never chose. The Kit is for new projects, with no unversioned clients to keep.
  - No `AddMvc()`: endpoints are minimal APIs; the versioned documents come from `Asp.Versioning.OpenApi` and ASP.NET Core's OpenAPI, not from Swashbuckle's MVC explorer.
  - The unversioned path is a second route group bound to the same version set inside the Module's group, so the header reader selects a route; upstream maps every route under `api/v{version:apiVersion}` only, where the header could never select one.
  - Sunset dates come from configuration; the plan rejects one computed from the current time (I.3, ADR 0005).
