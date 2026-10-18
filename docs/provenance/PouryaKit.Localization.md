# PouryaKit.Localization provenance

Adopted implementations in `PouryaKit.Localization`. Format and rules: [README](README.md).

Recording a Module's Feature assemblies through `IModuleServices` is the Kit's own Module seam, shared with every registration that names Feature assemblies, so it has no record here.

## Package dependencies

None. The package references the ASP.NET Core shared framework (`Microsoft.AspNetCore.App`), whose `Microsoft.Extensions.Localization` and request localization middleware it registers, and `PouryaKit.Cqrs`; `PackageGraphTests` holds it to that and `LicenceTests` to its closure, which is Cqrs's. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## English and Persian request cultures

- **Kit files:** `src/PouryaKit.Localization/LocalizationServiceCollectionExtensions.cs`, `src/PouryaKit.Localization/HeaderRequestCulture.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** d685de64c22dcb9be5b49206c6d9699da4ef6302
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Localization/LocalizationExtensions.cs` (lines 15 to 19: `AddLocalizationServices`), `src/BuildingBlocks/BuildingBlocks.Web/Extensions/LocalizationExtensions.cs` (lines 18 to 31: `UseLocalization`); read at the checkout's commit `736a6589550f4cabfa4bb50cadbf9fe62a4521a2`, where both cited files are byte-identical to the pin `d685de6` (`git diff` empty, checked 2026-10-18)
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #125
- **Intentional differences:**
  - English and Persian as the supported cultures and UI cultures with English the default, as upstream's `UseLocalization`; the Accept-Language header is the only provider, where upstream keeps the framework's query string and cookie providers too, so one source sets a request's culture.
  - The request culture is set by a startup filter the registration adds, where upstream's Host calls `UseLocalization` on its `WebApplication`, so a Host adds no middleware and one without the package has nothing to call.
  - No `ILocaleAccessor` service and no resource designer: a Feature injects the framework's `IStringLocalizer<T>`, which reads the `.resx` files beside `T` in its own assembly by the current UI culture.
  - Upstream's `ApplyCurrentCultureToResponseHeaders` is not set, so a response carries no `Content-Language` header.
