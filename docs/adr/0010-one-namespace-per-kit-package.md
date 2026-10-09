---
status: accepted
---

# A Kit package has one namespace, its own name, whatever its folders; application code's namespaces follow its folders

At `505fd44` (2026-10-09) every Kit package is flat: each file sits at the project root, in one namespace equal to the
package name, and the four largest roots hold 40 files (Cqrs, Messaging.Wolverine), 26 (Web) and 20
(Persistence.EFCore). The Proving Sample's Modules and the templates' content use Business Area and Feature folders
whose namespaces follow them. Nothing configures either side: no project sets a `RootNamespace`, and `.editorconfig`
has no namespace-and-folder setting. The owner's decision D14 of 2026-10-09 (spec #74) gives the four largest packages
folders by responsibility with no namespace change (ticket #78). From then on a Kit package's folders and its namespace
differ on purpose, while an editor gives a new file its folder's namespace by default, so the policy is recorded and
enforced here, on the flat tree, before any file moves.

It is recorded now because it is expensive to reverse. Changing a type's namespace is a binary and a source break, and
no forwarder exists for it, as one does for a type moved to another assembly
([library change rules](https://learn.microsoft.com/en-us/dotnet/core/compatibility/library-change-rules)). Before the
first public release the Kit has no consumer to break; after it, reversing this policy breaks every consumer.

## Decision

1. **A Kit package: folders by responsibility, one namespace per package, equal to the package name.** The namespace
   is the consumer's contract; the folder is the maintainer's map. A file in `src/PouryaKit.Cqrs/Pipeline/` declares
   `namespace PouryaKit.Cqrs;`. No extension member leaves its package's root namespace, so one `using` serves a
   package, and the skill's package annotations name exactly the `using` a consumer needs. This follows .NET's library
   guidance: few namespaces for the consumer, extension members in the namespace of their functionality
   ([extension methods](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/extension-methods)), and
   folders for the maintainer, as EF Core keeps its `Extensions` folder's classes in `Microsoft.EntityFrameworkCore`
   (`dotnet/efcore@2892c33 src/EFCore/Extensions/EntityFrameworkQueryableExtensions.cs:9`).
2. **Application code: namespaces follow folders**, in the Proving Sample, the templates' content and the test projects,
   as they already do. Three files are deliberate exceptions:
   - `samples/ModularMonolith/src/ModularMonolith.ServiceDefaults/Extensions.cs` is in `Microsoft.Extensions.Hosting`,
     Aspire's convention for the service defaults.
   - `tests/ModularMonolith.Tests/SameNamed/StubSameNamed.cs` is in the Stub Module's namespace,
     `ModularMonolith.Tests.Stub`: each of its types has the full name of one in the Stub's assembly, the
     equal-full-name proof.
   - `tests/PouryaKit.ArchitectureTests/Offending.Core/TextHelpers.cs` is in `PouryaKit.Core`: the fourth public helper
     `CoreTests` must reject.
3. **Each side is enforced once.**
   - Kit packages: `NamespaceTests` in `tests/PouryaKit.ArchitectureTests` asserts that every authored top-level type of
     each Kit package is in the namespace named after its assembly. It leaves out compiler-generated definitions by
     their marker (`CompilerGeneratedAttribute` or `GeneratedCodeAttribute`), not by their name, so a later generator
     is covered the same way. Its negative case is `Offending.Core`, whose four types, Core's sources and `TextHelpers`,
     are in `PouryaKit.Core` inside the assembly `Offending.Core`. `.editorconfig` sets
     `dotnet_style_namespace_match_folder = false` for `src/**`, so IDE0130 reports no folder mismatch there and
     the structure rule is the one check.
   - Application code: `.editorconfig` raises IDE0130 to a warning for the Proving Sample's Modules
     (`samples/ModularMonolith/src/Modules/**`) and the test projects (`tests/**`), and sets it to `none` for each
     exception. The build treats warnings as errors and runs code style (`TreatWarningsAsErrors` and
     `EnforceCodeStyleInBuild` in `Directory.Build.props`), so a namespace there that does not follow its folder fails
     the build.
   - The templates' generated solutions stay out: their shared props set no `EnforceCodeStyleInBuild`, and a hyphenated
     name such as `acme-shop` gives an invalid root namespace that makes the analyzer skip root files. `TemplateTests`
     and the sample's own build hold their shape; style enforcement for generated projects is a separate, later
     question.

## Considered Options

Each was measured on 2026-10-09 at `505fd44`.

- **Namespaces following folders.** Rejected: 65 public types would change namespace (Cqrs 30 of 41,
  Messaging.Wolverine 16 of 21, Web 12 of 15, Persistence.EFCore 7 of 12), and about 525 `using` lines with them,
  across the sample, the templates, the packaging consumers, the tests, the other Kit packages and the skill. 13 test
  lines that name types as strings and three logger categories that operators filter on would break. A Feature endpoint
  would need four Kit usings instead of three. Two namespaces would collide with internal type names (`Subscriptions`,
  `DelayedCommands`). And every later folder move would become a breaking change.
- **Keeping every package flat.** Rejected: it costs nothing today and leaves the responsibility map in the docs only,
  while 40-file roots grow each Wave.
- **A sub-namespace for the seams**, the types that are public only so another Kit package can use them, after EF
  Core's `Infrastructure` (`dotnet/efcore@2892c33 src/EFCore/Infrastructure/Annotatable.cs:4`). Rejected: it reaches
  five types only in Cqrs (14 seams) and Messaging.Wolverine (7). It would cut the `using PouryaKit.Cqrs;` directives
  from 41 to 27, but add a second using to 54 Kit, test and consumer files. It changes no sample or template file,
  because none names a seam, while the structure tests already reject an application that implements or calls one.
  `Extensions`, `Internal` and `Paging` sub-namespaces fail the same test: two to eight types each, or a using for every
  list Feature.
- **Folders by kind** (`Extensions/`, `Exceptions/`). Rejected: they are what makes CloudNativeKit's consumers average
  2.3 Kit usings per file and up to 20 (`mehdihadeli/food-delivery-microservices@c83bca3`, on
  `mehdihadeli/CloudNativeKit@e81d65e`).

## Consequences

- Ticket #78 moves the files of Cqrs, Messaging.Wolverine, Web and Persistence.EFCore into folders as renames: every
  full type name stays the same, and no consumer, template or packaging consumer changes. The other packages stay flat;
  `PouryaKit.Core` must, because `Offending.Core` compiles its sources as `src/PouryaKit.Core/*.cs`, without recursion.
- A Kit type's namespace is part of the public API. Changing one is a breaking change for the owner to decide, never a
  side effect of a move.
- `PouryaKit.Core.Tests`' `Lookup.cs` declares `PouryaKit.Core.Tests.Lookup` for the types the lookup reads. It was the
  one file the scoped warning found, and it moved into a `Lookup/` folder with its namespace kept.
- The Hosts and the service defaults are outside the warning's scope. On 2026-10-09 their files follow their folders,
  but for the service defaults' `Extensions.cs`, whose `none` already stands, so a wider scope needs no new exception.
- `.claude/rules/kit-code.md` states the policy in one line.
