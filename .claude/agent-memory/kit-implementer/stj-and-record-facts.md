---
name: stj-and-record-facts
description: Verified .NET 10 behaviour behind the Domain package - STJ ignores a base-type JsonConverter attribute, base init props survive ctor deserialization, positional derived record reuses an inherited Value
metadata:
  type: project
---

Probed on SDK 10.0.401 during ticket #6 (2026-10-05):

- System.Text.Json does NOT apply `[JsonConverter]` declared on an abstract base record to derived types; a derived typed ID serialized as `{"Value":...}`. So `TypedIdJsonConverterFactory` must be added once to `JsonSerializerOptions.Converters` by whichever package owns the options (Web #14, Messaging #15+).
- STJ deserializing a positional derived record still sets the base record's public `init` properties (DomainEvent `Id`, `OccurredOn`) after the constructor runs, so identity and time survive a round-trip with no `[JsonConstructor]`.
- `sealed record ProductId(Guid Value) : TypedId<Guid>(Value)` with a getter-only `Value` on the base compiles warning-free and synthesizes no second `Value` property.

**Why:** each decides the shape of a public Kit type; guessing wrong costs a public-API rewrite.
**How to apply:** when a later ticket wires JSON, EF or Dapper for typed IDs or Domain Events, build on these instead of re-probing; re-probe only after an SDK band change.
