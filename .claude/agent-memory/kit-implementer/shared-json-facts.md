---
name: shared-json-facts
description: Ticket #34 facts - ASP.NET JsonOptions is getter-only, Wolverine's STJ serializer mutates its options, how to read a stored envelope's message JSON, extension members under the IL reader
metadata:
  type: project
---

Verified in ticket #34 (2026-10-07), ASP.NET Core 10.0.12, WolverineFx 6.46.0:

- `Microsoft.AspNetCore.Http.Json.JsonOptions.SerializerOptions` is **getter-only**, so a Kit "factory" can only configure the instance ASP.NET hands over (`ConfigureHttpJsonOptions`), never replace it. Its defaults: `JsonSerializerDefaults.Web` (camelCase, case-insensitive, AllowReadingFromString) plus `UnsafeRelaxedJsonEscaping`.
- Wolverine `UseSystemTextJsonForSerialization(Action<JsonSerializerOptions>)` starts from `SystemTextJsonSerializer.DefaultOptions()` (camelCase, case-insensitive, trailing commas, comment skip, relaxed encoder), and the `SystemTextJsonSerializer` constructor **appends** `CustomJsonConverterForType` to the options it gets: never hand it a shared or already-used instance.
- Hence the Kit's shape: `options.UsePouryaKitJson()` (Domain, `JsonSerializerOptionsExtensions`) configures and returns a given instance; both frameworks call it in their own lambda.
- `wolverine_incoming_envelopes.body` is the whole envelope: `EnvelopeSerializer.Deserialize(byte[])` (public) gives `.Data`, the message JSON; `IWolverineRuntime.Options.DefaultSerializer.ReadFromData(type, envelope)` reads it back with the Host's real serializer. That proves serialization where the first local attempt only hands over the in-memory instance. Handled rows stay 5 min, so read them after `WaitUntilDeliveredAsync`.
- Without the Kit's options, Wolverine stored a typed ID as `{"value":"<guid>"}` and delivery still succeeded: only a representation check catches the drift.
- A C# 14 `extension(T)` member compiles to a static method on the outer class; `Il.CallsIn` resolves calls to it and `nameof(Outer.Method)` compiles. `GetConstructors(Static|NonPublic)` includes `.cctor`, so field initializers are scanned.
- `scripts/packaging/validate.py` refuses a dirty checkout and only builds the clean consumers (never runs them), so a consumer proves the packed API compiles; `single-module.cs` is copied as the single-module composition's `Program.cs` (ImplicitUsings and TreatWarningsAsErrors on). Commit first, run it, then add its PASS line to the docs.
- ASP.NET Core 10's HTTP JSON default encoder is already `UnsafeRelaxedJsonEscaping` (decompiled `JsonOptions.DefaultSerializerOptions`), so setting it in `UsePouryaKitJson` changed nothing for HTTP.

**Why:** each decided the #34 design or a test that would otherwise pass vacuously.
**How to apply:** for #42 (idempotency payloads: create options with `new JsonSerializerOptions().UsePouryaKitJson()`, the structure rule allows that and nothing else) and any later JSON or envelope-content test. Related: [[stj-and-record-facts]], [[messaging-delivery-facts]], [[architecture-test-facts]].
