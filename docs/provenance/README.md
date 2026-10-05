# Provenance records

Every implementation the Kit adopts from another codebase has a provenance record here, so that attribution holds and a later change upstream can be traced to the Kit file it affects. The licensing rules behind it are in plan section E, "Provenance and licensing", and G.

## Where records go

One file per Kit package, named after the package: `docs/provenance/PouryaKit.Domain.md`, `docs/provenance/PouryaKit.Cqrs.md`, and so on. The ticket that adopts the first implementation for a package creates its file. Each adopted implementation is one section in it, added in the same change as the code.

An idea that only shaped a design, with no code or test scenario taken, needs no record.

## Record format

```markdown
## <What was adopted, in GLOSSARY terms>

- **Kit files:** `src/PouryaKit.Domain/Result.cs`
- **Upstream:** https://github.com/<owner>/<repository>
- **Commit:** <full 40-character SHA>
- **Upstream paths:** `src/BuildingBlocks/Result.cs`, `src/BuildingBlocks/Error.cs`
- **Licence:** MIT
- **Adoption:** adapted
- **Ticket:** #6
- **Intentional differences:**
  - <what the Kit does differently, and why>
```

- **Kit files** and **Upstream paths** are repository-relative. A path on anyone's machine never appears in a record.
- **Commit** is the upstream commit that was read, not a branch name.
- **Licence** is an SPDX identifier, `none` when the upstream repository has no licence, or `unconfirmed` when its ownership is unconfirmed.
- **Adoption** is one of:
  - `copied`: the upstream code is taken as it is, apart from names and namespaces.
  - `adapted`: the upstream code is taken and changed; the differences say how.
  - `pattern`: no upstream code is taken. The Kit reimplements the design or ports test scenarios. This is the only level allowed for a source that is `none`, `unconfirmed` or not permissively licensed (CC BY-SA, Stack Overflow answers, Ultrabusiness).
- **Intentional differences** lists each deliberate change from upstream, so that an upstream fix can be judged against it. Write `none` when there are none.

## Notices

A `copied` or `adapted` record needs the upstream project's copyright notice and licence text in [`THIRD-PARTY-NOTICES.md`](../../THIRD-PARTY-NOTICES.md), once per upstream repository:

````markdown
## <Upstream project name>

- **Upstream:** https://github.com/<owner>/<repository>
- **Licence:** MIT

```text
<the upstream licence file, copyright line included, exactly as it reads at the recorded commit>
```
````

A `pattern` record needs no notice.
