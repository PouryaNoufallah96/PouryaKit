---
name: codegen-production-facts
description: Ticket #58 facts - Wolverine application assembly via the Kit, codegen write without Roslyn, Debug-restore trap, WebApplicationFactory with RunJasperFxCommands, check-env and the registry
metadata:
  type: project
---

Verified in ticket #58 (2026-10-07), WolverineFx 6.46.0, JasperFx 2.80.2; check the merged code before relying on it:

- **Application assembly through a library.** Wolverine and JasperFx pick the first stack frame outside their own assemblies; called through `UsePouryaKitWolverine` that is `PouryaKit.Messaging.Wolverine` (`dotnet run -- describe` shows `(application)` and the ServiceName). `TypeLoadMode.Static` loads generated types and the handler registry from `CodeGeneration.ApplicationAssembly` only, so the Kit sets `WolverineOptions.ApplicationAssembly = Assembly.GetEntryAssembly()`. Setting it on WolverineOptions (not JasperFxOptions) leaves the ServiceName as before.
- **Static start check** is `HandlerGraph.AssertPreBuiltTypesExist` at `StartAsync` (before `tryMigrateStorage`), chains only; a missing registry only warns. `AssertAllPreGeneratedTypesExist` is JasperFx's environment check: `check-env` fails on the missing `HandlerRegistryCodeFile`. `check-env --file x` writes which check failed; the console says only "Some environment checks failed!".
- **`codegen write`** builds, never starts the Host, and needs no `IAssemblyGenerator` (only `codegen test` does), so a Release build without the compiler can run it. For a Kit Host it writes `Internal/Generated/WolverineHandlers/GeneratedHandlerRegistry.cs` alone (empty arrays plus an AotRoots class).
- **Debug-only PackageReference trap:** `dotnet restore` (Debug evaluation) then `dotnet build -c Release --no-restore` puts `Wolverine.RuntimeCompilation.dll` and `JasperFx.RuntimeCompiler.dll` in the Release bin; restore with `-p:Configuration=Release` before a publish. A Release-built Host cannot run Development at all (the Dynamic guard throws), so any child process started from a Release test build must run Production.
- **WebApplicationFactory + `RunJasperFxCommands`:** the factory passes its settings as `--key=value` args; JasperFx's `ArgPreprocessor` splits on every `=`, so the parse fails ("Unknown argument or flag"). Without `JasperFxEnvironment.AutoStartHost = true` the Host never starts; with it JasperFx starts the Host before parsing and the failed parse is only console noise. Wolverine's `docs/guide/http/integration-testing.md` asks for it.
- In Production `/alive` answers 401 (ServiceDefaults maps health endpoints in Development only; the fallback policy covers the unmatched path).
- Shell: `python3 /dev/stdin <<'EOF'` with a short edit script passed; a longer `python3 - <<'EOF'` script was refused. `sed` on a `$VAR` path is refused: use the Read tool or a literal path.

**Why:** each cost a probe in #58, and the application-assembly one would have made Static mode useless for any application handler.
**How to apply:** for #54 close-out, a service template, or any change to Hosts, `Program.cs` or the Wolverine registration. Related: [[wolverine-6-spike-facts]], [[template-package-facts]], [[wolverine-spike-gotchas]].
