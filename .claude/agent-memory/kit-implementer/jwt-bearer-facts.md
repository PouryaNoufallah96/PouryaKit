---
name: jwt-bearer-facts
description: Ticket #39 facts - JwtBearer config binder sets ValidateAudience from config, ValidateOnStart hits every composed Host, a second started Host steals deliveries, mutation recipes for bearer validation
metadata:
  type: project
---

Verified in ticket #39 (2026-10-07), Microsoft.AspNetCore.Authentication.JwtBearer 10.0.12:

- **The framework's config binder replaces `TokenValidationParameters`** whenever `Authentication:Schemes:Bearer` has children (`JwtBearerConfigureOptions`, decompiled with `ilspycmd -p -o <dir> ~/.nuget/packages/microsoft.aspnetcore.authentication.jwtbearer/10.0.12/lib/net10.0/*.dll`). It runs before the `AddJwtBearer(delegate)`; `SigningKeys[n]` is kept only when its `Issuer` string-equals `ValidIssuer`/`ValidIssuers`. `JwtBearerPostConfigureOptions` copies `Audience` to `ValidAudience` and builds a `ConfigurationManager` from `Authority`, so a validator must run after post-configure (an `IValidateOptions` does).
- **`ValidateOnStart` on the Bearer options fails every Host `AppFixture.ComposeHost` builds**, even with `TestAuthentication` the default scheme: `ComposeHost` passes `TestIssuer.HostArguments` on the command line. `StartAsync` throws a single `OptionsValidationException` before hosted services start, so start-failure theories are cheap (~70 ms).
- **A second started Host shares the durable local queues.** A test that creates data through the fixture, then starts its own Host, can have that Host take a delivery and be disposed holding it; the next test's `ResetAsync` waits 30 s and times out, cascading. Only showed in the full run, never with `--filter-class`. Fix: `Fixture.WaitUntilDeliveredAsync` before starting and before disposing the second Host.
- Mutations: to red the 401 rows, bypass the validator and set `ValidateIssuer/ValidateAudience/ValidateLifetime/RequireSignedTokens = false` plus a `SignatureValidator` returning `new JsonWebToken(token)`; without `RequireSignedTokens = false` every token fails with IDX10253. `if (false)` mutations fail the build (CS0162 under TreatWarningsAsErrors); use `if (name.Length < 0)`.
- **Review F1-F4 (fix round):** a flags-only start check is not enough. In IdentityModel 8.19.2 a set `AudienceValidator`, `IssuerValidator(UsingConfiguration)`, `LifetimeValidator` or `SignatureValidator(UsingConfiguration)` runs *instead of* the default check (`IssuerValidatorAsync` is internal), and `RequireAudience = false` accepts a token with no `aud`. A later app `PostConfigure` that assigns `Events`, or a `JwtBearerEvents` subclass overriding `TokenValidated`, silently drops a normalization installed by `PostConfigure`. Fix: the installed delegate is an instance method of the internal `VerifiedClaims`, and the start check requires `Events.GetType() == typeof(JwtBearerEvents)` and `OnTokenValidated.Target is VerifiedClaims`.
- `RestoredPackage.Frameworks` holds direct framework references only: the JwtBearer package's transitive `Microsoft.AspNetCore.App` does not appear, so the PackageGraph row needs no framework.
- The brief's commit trailer named another model than the session's attribution reminder; the reminder's line was used.

**Why:** each decided a #39 design point or cost a full-suite round.
**How to apply:** any ticket touching the sample Host's authentication, options validation on start, or tests that start a second Host. Related: [[security-permission-facts]], [[web-aspnetcore-facts]], [[dead-letter-and-stale-time-facts]].
