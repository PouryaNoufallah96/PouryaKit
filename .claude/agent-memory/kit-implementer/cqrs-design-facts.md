---
name: cqrs-design-facts
description: Verified facts behind PouryaKit.Cqrs/Validation (ticket #7) - FluentValidation 12.1.1 licence and scanner API, generic Result failure trick, decisions awaiting main-session confirmation
metadata:
  type: project
---

Verified during ticket #7 (2026-10-05):

- FluentValidation 12.1.1 is `Apache-2.0` on nuget.org (published 2025-12-03), no dependencies on net8.0+. The core package has `AssemblyScanner.FindValidatorsInAssembly(assembly, includeInternalTypes: true)`, so no `FluentValidation.DependencyInjectionExtensions` (not allowed by section C) is needed.
- Building a failed `Result<T>` for any `T` needs no Domain change: inside a class that knows `TResult`, `static errors => errors` typed `Func<ImmutableArray<Error>, TResult>` uses Domain's implicit conversion for both `Result` and `Result<T>`.
- `IUnitOfWork.RunAsync` is non-generic (`Func<CancellationToken, Task<Result>>` to `Task<Result>`); the dispatcher maps the outcome back with `outcome as TResult ?? fail(outcome.Errors)`.
- Design calls made in #7 and reported as deviations (check the merged code before relying on them): rolled-back Operation fails with the first failed nested command's Errors; one outermost command per scope (second throws); a nested command exception marks rollback-only with `Operation.CommandThrew`.

- C# 14 `field` keyword on a get-only property assigned in only one of two constructors (`Result<T>.Value`) fails with CS9264 under nullable; `[field: MaybeNull]` plus `field!` in the getter fixes it. Assigning a get-only `field`-backed property in a constructor writes the backing field.
- Fix round (2026-10-05): the Operation now tracks whether the Unit of Work is active separately from command depth. A command sent from an authorizer or validator (before `RunAsync`) throws; queries there are fine. `IAuthorizer`/`IValidationStep` are invariant, and registration rejects authorizers not for one concrete operation.

**Why:** each cost investigation or is a seam #8, #10, #13 and #14 build on.
**How to apply:** when touching Cqrs, Validation or the Unit of Work, start from these; re-verify the licence when bumping FluentValidation.
