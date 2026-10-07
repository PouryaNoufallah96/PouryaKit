# ModularMonolith provenance

Packages that only the Proving Sample, `samples/ModularMonolith`, references. Format and rules: [README](README.md).

## Package dependencies

Checked on nuget.org on 2026-10-05; licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002), and no Kit package references them.

- `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.12, `MIT`, checked on 2026-10-05 (ticket #14). The Host authenticates its callers with it. Its closure, the `Microsoft.IdentityModel.*` packages and `System.IdentityModel.Tokens.Jwt` 8.19.2, is all `MIT`. The Host is an ASP.NET Core application (`Microsoft.NET.Sdk.Web`), so it takes its hosting from the shared framework and no longer references `Microsoft.Extensions.Hosting`.
- `Microsoft.AspNetCore.TestHost` 10.0.12, `MIT`, checked on 2026-10-05 (ticket #14), referenced only by `tests/ModularMonolith.Tests`. It serves the Host's HTTP pipeline in memory to the HTTP tests.
- `Microsoft.EntityFrameworkCore.Design` 10.0.12, `MIT`, referenced by the Host with `PrivateAssets="all"`, so it reaches no other project. `dotnet-ef` needs it in its startup project, the Host, whose services create the Modules' DbContexts. Its closure (`Humanizer.Core`, `Mono.TextTemplating`, `Newtonsoft.Json`, `Microsoft.Build.Framework` and the `Microsoft.CodeAnalysis` packages) is all `MIT`.
- The `dotnet-ef` 10.0.12 tool, `MIT`, pinned in `.config/dotnet-tools.json`. It generates the Modules' migrations and is never referenced at run time.
- `WolverineFx.RuntimeCompilation` 6.46.0, `MIT`, checked on 2026-10-05 (ticket #15), referenced by the Host only. Wolverine 6 compiles its runtime code with it, and a Host without it fails at start (ADR 0004, "Defaults chosen", 2); no Kit package references it. It adds `JasperFx.RuntimeCompiler` 5.0.0 and the `Microsoft.CodeAnalysis` packages, all `MIT`. With it and the Kit's messaging packages, the Host's restored closure is 72 packages: 62 `MIT`, 4 `Apache-2.0`, 3 `BSD-3-Clause` and 3 `PostgreSQL`; none is reciprocal or commercial.
- `WireMock.Net` 2.18.0, `Apache-2.0`, checked on nuget.org on 2026-10-07 (ticket #48), referenced only by `tests/ModularMonolith.Tests`: the controlled downstream HTTP server, with scenarios and a request journal, of the outgoing HTTP tests. It is test-only and ships in nothing, so ADR 0002, which governs the Kit's runtime dependencies, does not apply to its closure, and `LicenceTests` checks only the closures of the Kit packages under `src/`. That closure is not all permissive: `XPath2` 1.1.5 is `MS-PL`, a reciprocal licence, and `Fare` 2.2.1, `JmesPath.Net.Parser` 1.1.0, `Json.More.Net` 3.0.1, `JsonPath.Net` 3.0.2, `SimMetrics.Net` 1.0.5 and the old `Microsoft.AspNetCore.Http*`, `Microsoft.AspNetCore.WebUtilities` and `Microsoft.Net.Http.Headers` 2.3.x packages declare a licence URL instead of an expression. None of them may reach a Kit package or a Module.
- `xunit.v3` 4.0.1, `Apache-2.0`, checked against the NuGet cache on 2026-10-06, the test framework of `tests/ModularMonolith.Tests` and `tests/PouryaKit.ArchitectureTests`, and of the Kit's own test projects. No Kit package references it; `PouryaKit.Testing` references only its library form, `xunit.v3.extensibility.core`.

The Billing Module (ticket #17) adds no package: it references what Inventory references, except `PouryaKit.Persistence.Dapper`.

## Dead-letter and replay acceptance scenario

- **Kit files:** `tests/ModularMonolith.Tests/FailingDeliveryTests.cs`, `tests/ModularMonolith.Tests/Stub/StubCirculation.cs`
- **Upstream:** Ultrabusiness (`ultrabusiness_server_microservices`, a private repository with no public URL)
- **Commit:** a85eacd4db5e1af9b8349d49350be12397f24402
- **Upstream paths:** `src/Services/Treasury/Tests/Treasury.IntegrationTests/Features/IncomingChequeReceipt/Events/IncomingChequeReceiptCreatedForChequeDomainEventHandlerTests.cs` (`Deliver_WhenCreationFails_ShouldKeepSourceAndRecoverItsOriginalOccurrence`)
- **Licence:** unconfirmed
- **Adoption:** pattern
- **Ticket:** #17
- **Intentional differences:**
  - Kept: a delivery that keeps failing uses up its retries and is kept with its error, never recorded as processed, while the change that published it stays; once the cause is fixed and the delivery is put back, the original message is applied once.
  - Re-expressed for PostgreSQL and Wolverine: the delivery is set aside in Wolverine's dead-letter storage and replayed from there, through `PouryaKit.Testing`, instead of a retry counter and an error column on an outbox row.
  - Two subscriptions to one Integration Event, so the test also shows that the replay runs only the failed subscription's handler, and a second replay of the same delivery applies nothing because of its Receipt.
