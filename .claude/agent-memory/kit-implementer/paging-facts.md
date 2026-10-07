---
name: paging-facts
description: Ticket #35 - where page validation runs, Npgsql typed nulls, EF Contains translation, PageSql parameter naming, rebase onto a moved wave branch, guard-refused shapes
metadata:
  type: project
---

Verified in ticket #35 (2026-10-07), Dapper 2.1.89, Npgsql 10.0.3, EF Core 10.0.12:

- **Page validation runs in the Feature's validator** (`RuleFor(q => q.Page).Custom(...)` calling `PageDeclaration.Validate` and `AddFailure(new ValidationFailure(target, message) { ErrorCode = code })`), not in a Dispatcher edit: the advisor judged a Kit check beside `IValidationStep` a second validation path (G.8, ADR 0002) and a likely high review finding. The helpers re-run `Validate` and throw before any SQL, so a forgotten validator fails loudly. A reusable FluentValidation rule would belong in `PouryaKit.Validation` (left as an open question).
- A brief's `allowed_files` note "(new files; minimal edits to the four files named above)" meant: other existing files in those globs are editable; only the four parallel-ticket files stay minimal.
- **Npgsql types a null parameter only from its DbType**: `@x is null or col = @x` with an untyped null fails ("could not determine data type"); pass `DynamicParameters.Add(name, null, DbType.X)`. A `DateTimeOffset` must be UTC (`ToUniversalTime()`), or Npgsql refuses it for timestamptz.
- EF Core + Npgsql translates `Name.Contains(param)` to `name LIKE @p` with the parameter pre-escaped (case-sensitive, wildcards neutral), matching SQL `strpos(name, @p) > 0`. `product.CategoryId == new CategoryId(id)` inside a predicate factory translates: the funcletizer makes the typed ID a parameter.
- `ImmutableDictionary<,>.Empty` in a field initialiser trips IDE0301 under TreatWarningsAsErrors; write `[]`.
- Ardalis 9.3.1: `SpecificationEvaluator.Default.GetQuery(set, (ISpecification<T>)projectionSpec, evaluateCriteriaOnly: true)` gives criteria only for a projection spec; reject `OrderExpressions.Any()` and `spec is { Skip: > 0 } or { Take: > 0 }`.
- Ardalis 9.3.1 marks a Skip or Take that was never set with **-1**, not 0 or null (probed by running a spec): "set at all" is `spec is not { Skip: -1, Take: -1 }`. A `> 0` guard silently drops `Take(0)`.
- #35 review: normalise a typed value (DateTimeOffset to UTC) once, where the declaration parses it (`FilterField.Read`), not in one helper; otherwise EF Core passes a `+03:30` value Npgsql refuses. A test with a non-UTC offset through both helpers catches it.
- `validate.py` refuses a dirty tree and checks HEAD at the end: commit first, run it (~15 min, Release suite 520 tests), then record the PASS line in a docs-only commit. `consumers/<package>.cs` replaces the generated Program.cs; consumers are built, never run. Pre-check one against `src/` with a scratch Exe project (ImplicitUsings, TreatWarningsAsErrors).
- Mutating the Dapper helper to inline filter values as quoted literals makes the injection tests fail by PostgreSQL syntax/type errors, not by a visible leak; still red, but say so.
- **Moved wave branch:** commit the feature, then `git rebase feat/wave-2` (one commit stays one commit). #55 had added `PouryaKit.Core` as a direct reference of Dapper, EFCore, Cqrs and Web, so PackageGraph rows and the Dapper csproj conflicted; resolve to Core plus the new reference.
- The scratchpad is shared by every session: keep backups and harnesses in a ticket folder (`p35/`); generic `*.bak` names collide.
- Guard refused: `cd … && cat > file <<EOF` (use Write), `--filter-method "*X*"` in a compound line, and `cp …; git status` together. Plain `dotnet test --filter-class` alone passes.

**Why:** each decided the paging design or cost a retry.
**How to apply:** when touching paging, Dapper parameters, EF predicates or a rebase onto the wave branch. Related: [[dapper-facts]], [[persistence-ef-facts]], [[cqrs-design-facts]], [[worktree-and-check-gotchas]].
