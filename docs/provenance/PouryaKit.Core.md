# PouryaKit.Core provenance

Adopted implementations in `PouryaKit.Core`. Format and rules: [README](README.md).

`ImplementingTypes` is new Kit code, ticket #37, so it has no record here: plan I.3 rejects the reference projects' reflection helpers (1,782 lines returning `dynamic`) and keeps only the implementing-type lookup.

## Package dependencies

None shipped. `PouryaKit.Core` has no runtime package dependency and references no other Kit package, as plan section C lists. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Predicate composition

- **Kit files:** `src/PouryaKit.Core/PredicateBuilder.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Linq/PredicateBuilder.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #37
- **Intentional differences:**
  - The two predicates are combined with `Expression.AndAlso` and `Expression.OrElse`, which short-circuit, instead of the bitwise `Expression.And` and `Expression.Or` (upstream lines 25 and 40), which evaluate the right predicate even when the left one decided the result (plan I.3). The members are named `AndAlso` and `OrElse` to say so.
  - `Build`, which turns a property name, a comparison and a string value into a predicate, and its comparison helpers are left out: Core composes predicates and builds none from strings, and its `FilterComparison` belongs to upstream paging.
  - The substitution visitor replaces exactly the right predicate's parameter, instead of holding a public dictionary of substitutions, and both predicates are checked for null.

## Synchronization-context scope

- **Kit files:** `src/PouryaKit.Core/SynchronizationContextScope.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Utils/NoSynchronizationContextScope.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #37
- **Intentional differences:**
  - One public type, the scope itself, instead of a static class with a nested public `Disposable`, so Core keeps to three public types.
  - `Enter` takes the context to set, none by default, instead of always clearing it.
  - The scope is a `readonly ref struct`, so the compiler refuses one that lives across an `await` that could resume on another thread, and a `default` scope, never entered, restores nothing instead of clearing the context.
