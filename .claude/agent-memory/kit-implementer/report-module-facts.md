---
name: report-module-facts
description: A Module shipped as a Kit package (#140, PouryaKit.Reporting.Module): its connection name, migrations generated into a package, the structure tests it joins, testing fenced claims and retention over HTTP, and the role's refused Markdown writes
metadata:
  type: project
---

Check the merged code before relying on a design note.

## A Module in a Kit package (#140, 2026-10-18; EF Core 10.0.12, WolverineFx 6.48.3)

- **A packaged Module cannot be told its connection string's name.** `modules.Add<TModule>()` needs `new()`, `Configure(ModuleBuilder)` receives no configuration, and `AddPouryaKitDapper` throws on a second connection name in one Host, so a packaged Module that reads with Dapper must use the Host's name: `ReportModule.ConnectionName = "app"`, recorded as a limit and a gap for the owner. A name of its own, such as `reports`, would also have rippled into about fifteen tests that compose the sample Host with `--ConnectionStrings:app=` alone.
- **`dotnet ef migrations add Initial --project src/<package> --startup-project <sample Host> --context <internal DbContext> --output-dir Migrations --namespace <package> --no-build`** writes the migration and its designer to `Migrations/` but the model snapshot to a folder named after the namespace (`PouryaKit/Reporting/Module/`): move it, then remove the empty folders one by one. The generated `public partial class Initial` fails CS1591 in a Kit package: make it `internal sealed partial` (the designer and the snapshot carry no modifier); EF finds internal migrations. Its `columns: new[] { … }` fails IDE0300, since the main file has no auto-generated header: write `["a", "b"]`. Add `CreateReceiptTable(schema)` to its `Up` by hand, and `DropReceiptTable` to `Down`.
- **The structure tests take a packaged Module up by themselves.** `Sample.Modules` is every project the sample Host references that implements `IModuleDefinition`, Kit packages included, so `ModuleBoundaryTests`' Module list and its `HostPorts` (keyed by assembly name, here `PouryaKit.Reporting.Module`), `FeatureCodeTests.No_module_dbcontext_maps_the_message_stores_envelopes`' model count and `PersistenceTests`' table list all change; `NamespaceTests` judges it as a Kit package, one namespace. The modular-monolith composition in `inventory.json` lists the Kit packages the sample Host restores: add the Module and the storage provider.
- **A packaged Module's settings are an internal options type it binds itself** (`AddOptions<ReportOptions>().BindConfiguration("PouryaKit:Reports")`), as modules.md has every Module do: a public `PouryaKit*Options` the Host `Configure`s would be a fourth public type, which ADR 0001's rule (`ModuleBoundaryTests.HostPorts`, Composition Root and the ports the Host implements) does not admit. The ripples of a new Module in the sample Host: `CompositionCheckTests`' Module list, `PersistenceTests`' DbContext list (an internal DbContext by `Assembly.GetType`) and `NotificationTests`' second-Host row filter, which must leave out every recurring command's seeded queue.
- **`using PouryaKit.Reporting;` inside namespace `PouryaKit.Reporting.Module` is IDE0005**: the parent namespace's types are already in scope.
- **Dapper maps a text column to an enum and timestamptz to `DateTime`** into a private class with `init` properties; a positional record needs exact constructor types, so an enum or a `DateTimeOffset` parameter fails it. In a Module that is not tenant-scoped, `coalesce(tenant, '') = current_setting('pouryakit.tenant')` reuses the lease's binding and needs no typed null.

## Testing fenced claims and retention over HTTP (#140)

- **A class fixture on a database of its own with a RustFS container** (`ReportFixture`, as `MailFixture`): `AppFixture.ComposeHost` with `--PouryaKit:Storage:S3:*` arguments, the Host's ports `Replace`d by a test double, and the one `IFileStorage` descriptor swapped for a recording wrapper over its factory (`OneFileStorageCheck` still counts one). The sample Host's `appsettings.json` must carry S3 options, or every fixture Host fails `ValidateOnStart`; the client is built lazily, so no store needs to run for them.
- **Hold an attempt deterministically in the Host's layout port**: its first call awaits a `TaskCompletionSource`, bounded by a 30 s `WaitAsync` and released in the fixture's dispose too; the takeover's render runs unheld. A 2 s claim timeout and a 1 s retention cadence (test delivery polls every 100 ms) keep the class near 13 s.
- **Count a job's claims from the message store:** `select body from wolverine.wolverine_incoming_envelopes where received_at = 'local://<name>/'`, then `EnvelopeSerializer.Deserialize(body).Data` read as JSON (camelCase). Handled rows are kept, so the attempts per job show "taken over once" exactly: a mutation that schedules a takeover from every claim showed `[1, 2, 2]`.
- **Mutation runner traps:** a `continue` inside `try` skips a per-mutation `print` placed after the `finally`; deleting a `Schedule` line leaves a primary-constructor parameter unread (CS9113), so move its due time a year out instead.

## Docs from this role (#140)

- **The Write tool refuses a new `.md` file from this role as a "report file"**, even a project deliverable (a skill reference, a provenance record, this memory); Edit of an existing `.md` works. Write a scratchpad Python script that holds the text, and run it with the worktree as its argument (`p140/write_skillref.py`).
- **Trial a diagram's new node from the tip's source** (`git show HEAD:<source>` inside the trial script), not the worktree's re-ranged one, whose moved ranges do not fit the tip's files (`repository-evidence/line-out-of-range`); cite stand-in ranges that exist at the tip (`p140/nodes140.py`).

**Why:** each cost a build, a test run or a refused write in #140.
**How to apply:** when a Kit package holds a Module, generates EF migrations, proves a background job over HTTP, or writes docs from this role. Related: [[persistence-and-dapper-facts]], [[storage-and-s3-facts]], [[docs-verification-tools]], [[pdf-and-file-rendering-facts]].
