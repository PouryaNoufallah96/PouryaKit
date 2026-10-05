---
status: accepted
---

# The Operation Context is data set by whoever opens the Operation's scope

`PouryaKit.Cqrs` owns `OperationContext`, an immutable record of the Actor (`Actor.Person` or `Actor.System`), the
tenant or company, the correlation, the causation and the Operation's own `OperationId`. A handler, authorizer or
validator reads it by injection; nothing in the Kit reads `HttpContext` or any other ambient request. Whoever opens an
Operation's scope sets it once with `IServiceProvider.SetOperationContext`, which needs nothing but Cqrs. A Module's
Gateway takes it as a required argument and runs the Operation with exactly that context. A context belongs to one
Operation: its `OperationId` is a new `Guid.CreateVersion7()` when it is created, and its correlation is that
`OperationId` unless the caller gives one. Work an Operation causes runs with `Continue()`: the same Actor, tenant and
correlation, with the causing Operation's `OperationId` as its causation and a new `OperationId` of its own. The
owner delegated this decision to ticket #13 on 2026-10-05.

A synchronous question between Modules (G.5) is the one place a Gateway is called from inside an Operation: a Module
implementation never references another, so a handler asks through the owning Module's Contracts interface. The owning
Module registers that interface's implementation as scoped, so it is resolved in the asking Operation's scope, receives
the asking `OperationContext` by ordinary constructor injection, and passes `Continue()` to its own Gateway. In the
Proving Sample, `CatalogProducts` answers `ICatalogProducts` this way.

An Operation has no default context. The Gateway refuses a null one, and resolving `OperationContext` in a scope whose
opener set none throws, so an Operation never runs as an anonymous or system Actor by accident. Independent system work
creates its context with a named `Actor.System(...)` (G.6).

## Considered Options

- **Capture the context from the calling scope at the Gateway.** Rejected: the Gateway would have to be resolved in
  the caller's scope, so it could not be a singleton. Callers outside any scope (a hosted service, a test reaching the
  Host's root provider) would then fail scope validation, which is on in Development and which story 53 asks for in
  every environment. The capture
  that is needed happens one step earlier, in the Contracts implementation, where it is plain injection.
- **An ambient binding, as the clock is bound (ADR 0005).** Rejected: an `AsyncLocal` context is hidden state that flows
  into every task started under it, including work that is not a continuation, and an Operation started with no
  binding silently sees none or an outer one. It would also have to be bound by the dispatcher or by every scope opener.
  The clock accepts this because the system clock is a correct fallback; for identity no fallback is correct. The same
  idea in Ultrabusiness (`a85eacd`,
  `src/BuildingBlocks/MainArchitecture/Infrastructure/MainArchitecture.Infrastructure/Identity/InternalRequestContext.cs`)
  is opt-in and loses the Actor and the trace when work crosses the outbox (plan B.1, row 16).
- **Let the Gateway stamp a new `OperationId` on every context it receives.** Rejected: the handler would then read a
  context other than the one its caller passed, and an unstarted context would need a placeholder `OperationId`.
- **An Operation with no context runs as a system Actor.** Rejected by G.6: system identity must be explicit.

## Consequences

- Every Gateway call names its context. Test code that does not care uses `PouryaKit.Testing`'s
  `GatewayTestExtensions`, which run each call as a new Operation of the named `GatewayTestExtensions.TestActor`.
- A context passed to a Gateway is that Operation's. A caller that runs two Operations creates two contexts, or runs
  the second with `Continue()` when the first caused it; passing one context twice gives both the same `OperationId`.
- **#14 (Web)** builds the context from the authenticated `ClaimsPrincipal` at the HTTP boundary and sets it on the
  request's scope with `SetOperationContext`, so an endpoint injects `OperationContext` and passes it to the Gateway.
  Web references Cqrs only, which is enough. An unauthenticated endpoint still needs an explicit Actor.
- **#15 and #16 (delivery)** write the Actor, tenant and correlation, and the causing Operation's `OperationId`, into the
  message headers, and restore them in the scope the delivery step opens for each attempt with
  `new OperationContext(actor, tenant, correlationId, causationId)` and `SetOperationContext`, before the handler runs.
  `Messaging.Wolverine` references Cqrs only, which is enough. That step must also tag its scope with the receiving
  Module so the Unit of Work routes to it; the tag is internal to `PouryaKit.Modularity` today, and how a package that
  does not reference Modularity sets it is left to #15 and #16.
- **#11 (audit)** reads the Actor from the injected `OperationContext`.
