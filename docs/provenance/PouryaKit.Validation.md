# PouryaKit.Validation provenance

Adopted implementations in `PouryaKit.Validation`. Format and rules: [README](README.md).

## Package dependencies

`FluentValidation` 12.2.0, licence `Apache-2.0` as published on nuget.org (licence expression in the package's nuspec; 12.1.1, published 2025-12-03, checked 2026-10-05; 12.2.0 checked 2026-10-24, ticket #167). It is neither reciprocal nor commercial, so it meets ADR 0002's dependency policy. It is the package's only third-party reference (plan section C); `FluentValidation.DependencyInjectionExtensions` is not used.

## Validation step

- **Kit files:** `src/PouryaKit.Validation/FluentValidationStep.cs`, `src/PouryaKit.Validation/ValidationServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Validation/Middlewares/ValidationBehavior.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #7
- **Intentional differences:**
  - Returns one failed Result holding every failure of every validator instead of throwing `ValidationException` (G.1, G.8).
  - It is the validation step of the Kit's fixed pipeline (`IValidationStep<TOperation>`), not a MediatR pipeline behaviour, and it runs for commands and queries alike.
  - Each failure becomes a validation Error: FluentValidation's error code is its code (`Validation.Failed` when there is none), its message the message, and its property name the Error's target.
  - Validators are found with FluentValidation's `AssemblyScanner` in the assemblies given, internal types included, without `FluentValidation.DependencyInjectionExtensions`.
  - The per-request log line is dropped; the dispatcher logs each Operation's outcome. The cancellation token reaches each validator.
