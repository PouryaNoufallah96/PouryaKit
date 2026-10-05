---
paths:
  - "src/**"
  - "tests/**"
  - "samples/**"
---

# Kit code

## Design

- **Package boundaries.** Plan section C, "Allowed references", says what each package may reference. A project or third-party reference outside that table is an owner decision.
- **Dispatch.** Commands and queries go through the Kit's own dispatcher and its fixed pipeline (ADR 0002).
- **Isolation.** One host container; a Module is isolated by its assembly, `internal` types, its own DbContext and schema, and architecture tests (ADR 0001).
- **Commit.** The Unit of Work owns every commit, in the fixed sequence in plan section C. Handlers and Feature code return a `Result` and leave saving, committing and the Outbox to it (ADR 0003).
- **Failures.** Expected business failures are a `Result`; exceptions are for bugs and infrastructure (G.1).
- **Reactions and communication.** A reaction is a Domain Event Handler, a Notification Handler or an Integration Event Handler, and the three stay distinct (G.2). Modules talk to each other only in the ways G.5 lists.
- **Also settled.** Typed IDs (G.3), repositories and specifications (G.4), authorization at trust boundaries (G.6), explicit mapping with no mapper (G.7), validation as one pipeline step (G.8).
- **Adopted code.** Extend the existing seam. Code adopted from a reference project follows plan section E, "Provenance and licensing".
- **Current idioms.** .NET 10 only and the latest C# (plan section F, item 6): write the C# 14 and .NET 10 form where one exists, such as extension members, the `field` keyword, collection expressions, primary constructors, `System.Threading.Lock`, `TimeProvider` and `Guid.CreateVersion7`. An older idiom where a current one fits is a review finding.
- **Smallest design that fits the ticket.** A comment earns its line only by stating a non-obvious constraint that names and types cannot carry.

## Tests

- **Test behaviour that matters:** an invariant, a failure path, an ordering, or a guarantee the ticket states. One test per behaviour; leave out coverage that restates another test or the compiler.
- **Runtime proof.** A ticket that claims a runtime guarantee is proven against PostgreSQL in Testcontainers (H.5). A Phase is done when its tests pass there, never on compilation alone (section E).
- **Stack.** xUnit v3, Testcontainers and Respawn, through `PouryaKit.Testing`; one test project per package plus `ArchitectureTests` under `tests/` (section C). Test projects run on Microsoft.Testing.Platform (set in `global.json`) with the `xunit.v3` package only, never the VSTest packages (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`); a test project with no test fails the run.
- **Spikes.** A Spike is deleted once its decision is recorded; one that proves an invariant worth protecting becomes a permanent integration or architecture test (G.10).
