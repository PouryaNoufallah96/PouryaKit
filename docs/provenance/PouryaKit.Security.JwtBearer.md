# PouryaKit.Security.JwtBearer provenance

Adopted implementations in `PouryaKit.Security.JwtBearer`. Format and rules: [README](README.md).

`PouryaKitJwtBearerOptions`, the start check of the bearer options (`JwtBearerValidation`) and `PouryaKitClaimTypes` in `PouryaKit.Security` are new Kit code, so they have no record here.

## Package dependencies

Checked on nuget.org on 2026-10-07 (ticket #39); licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12, `MIT`, the latest stable version, for `net10.0`. It was already pinned in `Directory.Packages.props` for the sample Host, which now receives it through this package instead. Its nuspec declares the framework reference `Microsoft.AspNetCore.App` and one dependency, `Microsoft.IdentityModel.Protocols.OpenIdConnect` 8.19.2; the restored closure adds `Microsoft.IdentityModel.Abstractions`, `.JsonWebTokens`, `.Logging`, `.Protocols` and `.Tokens` and `System.IdentityModel.Tokens.Jwt`, all 8.19.2, and `Microsoft.Bcl.Cryptography` 10.0.2, all `MIT`; `LicenceTests` checks the whole closure.

`PouryaKit.Security.JwtBearer` also references `PouryaKit.Security`, as plan section C lists since ticket #39, and does not reference `PouryaKit.Web`; `PackageGraphTests` keeps Web out of its closure. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Bearer registration and claim normalization

- **Kit files:** `src/PouryaKit.Security.JwtBearer/JwtBearerServiceCollectionExtensions.cs`, `src/PouryaKit.Security.JwtBearer/VerifiedClaims.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Security/Jwt/Extensions/ClerkExtensions.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #39
- **Intentional differences:**
  - No provider: upstream reads a Clerk authority and authorized party from its own options and checks Clerk's `azp` claim (`ClerkExtensions.cs:95-133`); the Kit binds the framework's own `Authentication:Schemes:Bearer` section and has no provider SDK or provider-specific claim.
  - Audience validation is never off: upstream sets `ValidateAudience = false` with a suppressed analyzer warning (`ClerkExtensions.cs:111-113`); the Kit's Host fails at start unless audience, issuer, signature and lifetime are validated against configured values.
  - The token keeps its own claim names and the Kit's `OnTokenValidated` writes the configured Actor and tenant claims under fixed types, where upstream only changes `NameClaimType` (`ClerkExtensions.cs:114`). The application's own `OnTokenValidated` is kept and runs after it, instead of `Events` being replaced (`ClerkExtensions.cs:117`).
