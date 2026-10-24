# PouryaKit.Testing provenance

Adopted implementations in `PouryaKit.Testing`. Format and rules: [README](README.md).

`TestClock` and `GatewayTestExtensions` are new Kit code, so they have no adoption record here.

## Package dependencies

Checked on nuget.org on 2026-10-05; licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Testcontainers.PostgreSql` 4.16.0, `MIT` (4.15.0 until ticket #167, 2026-10-24, as the three below). Its closure on `net10.0` (`Testcontainers`, the `Docker.DotNet.Enhanced` packages, `SSH.NET`, `SharpZipLib`, `BouncyCastle.Cryptography`) is all `MIT`.
- `Respawn` 7.0.0, `Apache-2.0`, with no dependencies.
- `Microsoft.Extensions.TimeProvider.Testing` 10.11.0, `MIT` (10.10.0), with no dependencies on `net10.0`. It supplies `FakeTimeProvider` for `TestClock` (ADR 0005).
- `xunit.v3.extensibility.core` 4.0.2, `Apache-2.0` (4.0.1), with `xunit.v3.common` 4.0.2, `Apache-2.0`. It is the library form of xUnit v3; the test runner packages stay in test projects.
- `PouryaKit.Messaging.Wolverine.EFCore`, as plan section C lists (ticket #15). With it the package's restored closure is 101 packages: 90 `MIT`, 5 `Apache-2.0`, 3 `BSD-3-Clause` and 3 `PostgreSQL`, all recorded with the packages that bring them.
- `WolverineFx.RuntimeCompilation` 6.52.0, `MIT` (6.48.3), checked on 2026-10-07 (ticket #58) at 6.46.0 and moved on 2026-10-09 (ticket #75 to 6.48.2, its follow-up to 6.48.3) and on 2026-10-24 (ticket #167 to 6.52.0): the version of every Wolverine package the Kit pins, on which it depends exactly. ADR 0004 lets the Testing package reference it, so a test Host can run Development, where Wolverine compiles its handler code at run time, from any build, while the Hosts reference it in Debug builds only. It brings `JasperFx.RuntimeCompiler` 5.0.0 and the `Microsoft.CodeAnalysis` scripting and Visual Basic packages, all `MIT`. The package's restored closure is now 111 packages: 98 `MIT`, 7 `Apache-2.0`, 3 `BSD-3-Clause` and 3 `PostgreSQL`; the seventh `Apache-2.0` is `System.Linq.Dynamic.Core` 1.7.4, which `JasperFx` brings since ticket #167.

## PostgreSQL test fixture

- **Kit files:** `src/PouryaKit.Testing/HostFixture.cs`, `src/PouryaKit.Testing/HostTest.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `tests/shared/Cybernod.Tests.Shared/Fixtures/PostgresContainerFixture.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #8
- **Intentional differences:**
  - The fixture also hosts the application: the consuming test builds its Host, and the fixture applies every Module's migrations, found as the DbContexts registered in the Host, and then starts it.
  - The data is reset before each test by a base test class; a reset failure fails the test instead of being written to the message sink.
  - No configuration binding, container name or database-creation check; the image is pinned.
  - Operations are sent through a Module's Gateway, as the application sends them.

## Delivery assertions

- **Kit files:** `src/PouryaKit.Testing/HostFixture.cs`, `src/PouryaKit.Testing/StoredDeliveries.cs`, `src/PouryaKit.Testing/Delivery.cs`, `src/PouryaKit.Testing/DeadLetter.cs`, `src/PouryaKit.Testing/DeliveryAssertionException.cs`, `src/PouryaKit.Testing/TestDeliveryServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/meysamhadeli/woo
- **Commit:** 149a3cf168496c3bec85945f83ad222e43720e4d
- **Upstream paths:** `src/TestBase/TestBase.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #15
- **Intentional differences:**
  - Kept: the stored envelopes are read before the Operation runs, as a baseline, and what it caused is what was stored after it.
  - No Wolverine tracked session: one ends when a handler's execution finishes, before a stored retry runs. The Kit waits on the message store instead, until no stored delivery is pending, so retries, dead letters and the deliveries that deliveries cause are all waited for.
  - Waiting fails when the Operation stored no delivery, and a separate assertion checks that a rolled-back Operation stored none. `mehdihadeli/food-delivery-microservices@c83bca3 tests/Shared/Tests.Shared/Fixtures/SharedFixture.cs` passes a wait for messages when none was sent (plan section B.2, "Test support").
  - The reset before each test first waits until no delivery is pending, then deletes the stored deliveries but keeps the message store's own node records, which belong to the running Host.
  - Deliveries run at test speed: Solo durability, two retries 100 ms apart, and the store polled every 100 ms.
  - Dead letters are read and replayed through Wolverine's own dead-letter storage (`IDeadLetters`), as an application inspects them, with the subscription taken from the queue each was received at (ticket #17). A replay fails when its delivery is not a dead letter, and waiting counts a dead letter marked for replay as pending until it is handled or set aside again.
