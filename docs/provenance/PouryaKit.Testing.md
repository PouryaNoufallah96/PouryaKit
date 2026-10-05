# PouryaKit.Testing provenance

Adopted implementations in `PouryaKit.Testing`. Format and rules: [README](README.md).

## Package dependencies

Checked on nuget.org on 2026-10-05; licence expressions from each package's nuspec. None is reciprocal or commercial (ADR 0002).

- `Testcontainers.PostgreSql` 4.15.0, `MIT`. Its closure on `net10.0` (`Testcontainers`, the `Docker.DotNet.Enhanced` packages, `SSH.NET`, `SharpZipLib`, `BouncyCastle.Cryptography`) is all `MIT`.
- `Respawn` 7.0.0, `Apache-2.0`, with no dependencies.
- `Microsoft.Extensions.TimeProvider.Testing` 10.10.0, `MIT`, with no dependencies on `net10.0`. It supplies `FakeTimeProvider` for `TestClock` (ADR 0005).
- `xunit.v3.extensibility.core` 4.0.1, `Apache-2.0`, with `xunit.v3.common` 4.0.1, `Apache-2.0`. It is the library form of xUnit v3; the test runner packages stay in test projects.

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
