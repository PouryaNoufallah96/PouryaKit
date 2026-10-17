---
status: accepted
---

# The Operation Context is data set by whoever opens the Operation's scope

`PouryaKit.Cqrs` owns `OperationContext`, an immutable record of the Actor (`Actor.Person` or `Actor.System`), the
tenant or company, the correlation, the causation and the Operation's own `OperationId`. A handler, authorizer or
validator reads it by injection; nothing in the Kit reads `HttpContext` or any other ambient request. Whoever opens an
Operation's scope begins it once with `IServiceProvider.BeginOperation(context, module)`, which needs nothing but Cqrs.
A Module's Gateway takes the context as a required argument and runs the Operation with exactly that context. A context belongs to one
Operation: its `OperationId` is a new `Guid.CreateVersion7()` when it is created, and its correlation is that
`OperationId` unless the caller gives one. Work an Operation causes runs with `Continue()`: the same Actor, tenant and
correlation, with the causing Operation's `OperationId` as its causation and a new `OperationId` of its own. A
continuation is trusted (G.6): the Kit adds no authorization of its own for it, while an `IAuthorizer<T>` registered for
the continued operation still runs, with the original Actor, and can recognise a continuation by its `CausationId`. The
owner delegated this decision to ticket #13 on 2026-10-05.

The Module an Operation runs in is a fact of its scope, not of the context: work an Operation causes may run in another
Module, as a message's receiver does. `BeginOperation` records it on the scope's `Operation` (`Operation.Module`, a
`Type`, null in an application without Modules), which the GLOSSARY already defines as one command or query running
inside one Module, and `PouryaKit.Modularity` routes the Unit of Work by it. A separate scoped type for the Module was
the alternative; it would be a second per-scope holder beside `Operation` for one value. The scope's context is held
on the same `Operation`, so beginning an Operation is one call that sets both, once.

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
  Host's root provider) would then fail scope validation, which `AddPouryaKitModules` turns on in every environment.
  The capture that is needed happens one step earlier, in the Contracts implementation, where it is plain injection.
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
  `GatewayTestExtensions`, which run each call as a new Operation of the named `GatewayTestExtensions.TestActor`. The
  owner accepted these on 2026-10-05: a test is independent system work under an explicit, named identity, and the
  overloads exist only in `PouryaKit.Testing`.
- The dispatcher does not require a context (owner, 2026-10-05). An Operation sent through `IDispatcher` from a scope
  nobody began runs, and fails clearly only where something reads its `OperationContext`. That is enough for an
  application without Modules, which begins each scope's Operation itself.
- A context passed to a Gateway is that Operation's. A caller that runs two Operations creates two contexts, or runs
  the second with `Continue()` when the first caused it; passing one context twice gives both the same `OperationId`.
- **#14 (Web)** builds the context from the authenticated `ClaimsPrincipal` at the HTTP boundary and sets it on the
  request's scope with `BeginOperation`, so an endpoint injects `OperationContext` and passes it to the Gateway.
  Web references Cqrs only, which is enough. An unauthenticated endpoint still needs an explicit Actor.
- **#15 and #16 (delivery)** write the Actor, tenant and correlation, and the causing Operation's `OperationId`, into the
  message headers, and restore them in the scope the delivery step opens for each attempt with
  `new OperationContext(actor, tenant, correlationId, causationId)`, and begin it with
  `BeginOperation(context, receivingModule)` before the handler runs, so the Unit of Work routes to the receiving
  Module. `Messaging.Wolverine` references Cqrs only, which is enough.
- **#11 (audit)** reads the Actor from the injected `OperationContext`.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands. Ticket #14 fixed these facts of the context `PouryaKit.Web` opens each request's Operation
with (`src/PouryaKit.Web/WebEndpointRouteBuilderExtensions.cs`), which a project's authorization and tracing build
on.

- **A configured tenant claim missing on the token stays a null tenant.** When `PouryaKitWebOptions.TenantClaim` is
  set and an authenticated caller's identity lacks that claim, the Operation runs with a null tenant; the caller is
  not refused. Some callers legitimately have no tenant, and what a null tenant may see is the tenant filter's
  business, not the HTTP boundary's. **Security-relevant:** the Kit does not refuse such a caller, so an application
  in which every caller must carry a tenant enforces that itself, in its authentication scheme or an `IAuthorizer<T>`
  (`PouryaKitWebOptions.TenantClaim`;
  `WebTests.The_tenant_claim_fills_the_tenant_and_a_caller_without_it_runs_with_none`).
- **Every request starts a new correlation chain.** The context is created with no correlation, so its correlation is
  its own `OperationId` and it has no causation; no incoming header sets either. Accepting a caller's correlation
  would move a trust boundary (G.6), so it is a decision to take explicitly
  (`WebTests.The_handler_reads_the_authenticated_caller_as_the_actor_of_a_new_chain_of_work`).
- The explicit Actor an unauthenticated endpoint needs (Consequences, #14) is named with `AllowAnonymousAs(actor)`;
  plain `AllowAnonymous` on a Module's endpoint fails when the endpoint is built
  (`WebTests.A_module_endpoint_that_allows_anonymous_callers_without_naming_an_actor_fails_the_host_at_start`).

## Amendment (2026-10-07, ticket #57: only the Kit establishes continuation trust)

The decision above stands, but a causation no longer makes a context trusted. Through Wave 1, `PouryaKit.Security`'s permission check treated any context with a `CausationId` as a trusted continuation. The public constructor accepts any causation, and so does `Continue()` on any context, so application code could build a context that skipped the check. The owner decided on 2026-10-07 that the authorization and continuation design stays (G.6: work an authorized Operation creates is a trusted continuation, not re-authorized as the original user), and that only Kit code establishes that trust. Where the sentences above say a continuation is recognised by its `CausationId`, read `IsTrustedContinuation` instead.

- **The trust mark.** `OperationContext.IsTrustedContinuation` is public and read-only. `CausationId` stays public and only records the chain. The permission check and any application authorizer recognise a trusted continuation by the flag, never by `CausationId`. The mark has no setter, no public init accessor and no constructor parameter, so neither application code nor a serializer can set it, and a record `with` can only clone it. The public constructor never sets it, whatever causation it is given.
- **`Continue()` inside the running, authorized Operation is trusted (G.6).** `Continue()` gives a trusted context in one case only. It must be called on the exact context instance of an Operation that the dispatcher is running, after that Operation's authorization checks passed. It must also be called from inside that Operation: its handler, a Domain Event Handler, the Unit of Work's steps, or a Contracts implementation the handler calls, as `CatalogProducts` does. On any other context, it records the causation and gives an untrusted context. That covers a context that never ran, a context whose Operation has ended, and a context application code built. Inside an authorizer or a validator, even one nested in an authorized Operation, `Continue()` is not trusted either, because what those run is not yet authorized. The dispatcher holds the authorized Operation's context ambiently (`AuthorizedOperation`, internal to Cqrs), and only to check this claim. The context itself is still passed explicitly, so the ambient binding that this ADR rejects is not reintroduced. A task started inside the Operation that outlives it sees it end. `BeginOperation` alone grants nothing, because it is public.
- **A restore from material the Kit wrote is trusted.** Outside a running Operation, the only member that sets the mark is `OperationContext.RestoreTrusted(actor, tenant, correlationId, causationId)`, which is internal to Cqrs. Its one caller today is the delivery step of `PouryaKit.Messaging.Wolverine` (`OperationContextHeaderExtensions.ContinuedOperationContext`). That step restores the context from the Outbox headers the Kit wrote when the causing, authorized Operation committed. This replaces the public constructor that the Consequences above name for #15 and #16. `Continue()` on a restored context, inside the delivery's running Operation, is trusted too. The trusted-peer binding of `PouryaKit.Web` (#48) must call `RestoreTrusted` and add no other way to set the mark. Delayed work (#44) stays trusted only when it is delivered through this restore path.
- **The one `InternalsVisibleTo`.** Cqrs opens its internals to `PouryaKit.Messaging.Wolverine` and `PouryaKit.Web`, and to no other assembly. Security has to read the mark, and Kit packages other than Cqrs have to set it. Without `InternalsVisibleTo`, an application can call every public entry point that Kit delivery can call. An in-process secret would not help, because application code can read configuration and the container. The Kit uses this `InternalsVisibleTo` for `RestoreTrusted` only. `TrustedRestoreTests` in `tests/PouryaKit.ArchitectureTests` keeps the list to these two assemblies. It also fails when either assembly calls any other internal member of Cqrs.
- **Limits.**
  - A handler in an authorized Operation can continue into protected work that its Actor holds no permission for. One example is an Operation that declares no permission asking another Module a question that does declare one. That is G.6, not a gap: the work runs on the authority of the Operation that caused it, so which permissions an Operation declares, and what its handler continues into, are application design that review judges.
  - An assembly named after one of the two opened assemblies, or code that uses reflection, can get past the rule; the Kit does not defend against code that does this on purpose.
  - A delivery is trusted because the message store is trusted: any code that writes an envelope carrying the Kit's headers to a subscription's queue is restored as a trusted continuation.

Proven at the Gateway seam ([acceptance](../acceptance.md#wave-2-continuation-trust-only-from-the-kit-ticket-57)) by these tests:

- `SecurityTests.A_context_application_code_builds_with_a_causation_is_not_trusted_and_its_actor_is_checked`
- `SecurityTests.A_context_continued_outside_the_operation_that_ran_it_is_not_trusted`
- `SecurityTests.A_running_authorized_operation_continues_into_a_permission_protected_query_without_its_actor_being_checked_again`
- `OperationContextTests.A_question_to_another_module_continues_the_asking_operations_context`

The two #38 delivery tests show that the delivery seam keeps its trust.

## Amendment (2026-10-07, ticket #48: calls between Kit Hosts)

The decision above stands, and so does "every request starts a new chain of work" for every caller but one: a
configured, authenticated peer Host. Spec #33 (user stories 39 to 42) asked for HTTP continuations between Hosts; the
Host's trust boundary (G.6) moves only as far as the application's own peer list.

- **A trusted peer continues an Operation; nobody else does.** `PouryaKitTrustedPeerOptions.Peers` lists the peers by
  the value an authenticated identity carries in `PouryaKitWebOptions.ActorClaim`; it is empty by default. Only when an
  authenticated identity of the request names a listed peer does Web build the request's context from the Kit's
  headers, as a delivery does from a message's: `new OperationContext(actor, tenant, correlationId, causationId)`, with
  an `OperationId` of its own, in the same context creation as before, so no middleware is added. A peer's request
  without the headers runs as the peer in a new chain; one with incomplete or unreadable headers is refused with 400.
  Every other caller, authenticated or anonymous, keeps its claims-derived identity and a new chain, and its headers
  are ignored: a header never identifies a peer, so a public caller cannot set an Actor, a tenant, a correlation or a
  causation (`TrustedPeerTests`). A continuation is trusted as before (G.6), so the permission check stands aside for it
  as for a delivered one; an authorizer the application registered still runs.
- **The headers are Messaging's vocabulary.** The names and format are those of `PouryaKit.Messaging`'s
  `OperationContextHeaders`. Web does not reference Messaging (plan section C), so it states the same names in the
  internal `OperationContextHttpHeaders`, and the tests write and read them by Messaging's constants, which keeps one
  vocabulary.
- **Outgoing calls carry the context explicitly and never the bearer token.** `AddPouryaKitOperationContext()` writes
  the context a request was given with `WithOperationContext(context)`: the Actor, tenant and correlation, and the
  calling Operation's `OperationId` as the causation, on every attempt. `HttpClientFactory` builds its handlers in a
  scope of its own, and reading the incoming request instead (`IHttpContextAccessor`, as
  `cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Security/Utilities/HttpClientAuthorizationDelegatingHandler.cs`
  does) is the ambient read this ADR rejects; it would also leave a delivery or a scheduled job with no context. The
  handler copies nothing from an incoming request, so the caller's `Authorization` header and bearer token are never
  forwarded (G.6, plan I.3; `OutgoingHttpTests`); a peer authenticates the calling Host by the Host's own credentials.
- **Retry never replays a write unless the client says it is safe.** The resilience handler is the standard
  `AddStandardResilienceHandler()`, which needs no Aspire or ServiceDefaults. By default it retries only GET, HEAD,
  OPTIONS and TRACE, an allow-list, so an extension write such as PROPPATCH is sent once too; every method is retried
  only with `HttpReplay.AllRequests`, the explicit choice of a client whose downstream makes writes replay-safe, and any
  other `HttpReplay` value fails the registration. A Host with ServiceDefaults keeps one resilience pipeline per client
  (#52).
- **A context value that could break a header is never sent.** An Actor id or tenant is sent only when it is 1 to 256
  visible ASCII characters with spaces only between them; any other value, a CR or LF that would start another header
  included, fails the call before anything is sent, naming the header. The receiving Host applies the same rule to a
  trusted peer's headers and answers 400 (`OutgoingHttpTests`, `TrustedPeerTests`).

## Amendment (2026-10-07, ticket #42 follow-up: the context in the idempotency key)

The decision above stands. The owner decided on 2026-10-07 that these rules of the context belong here; the
idempotency slot itself is recorded in [ADR 0002's amendment](0002-no-mediator-dependency.md#amendment-2026-10-07-tickets-42-and-43-the-optional-idempotency-and-caching-slots).

- **The request id is Operation Context data and part of the idempotency contract.** Whoever opens the Operation sets
  it on the context; an idempotent command's key is the tenant, the Actor, the command and the request id.
- **A tenant is null or not blank.** An Operation without a tenant has a null one, which a key stores as one fixed
  sentinel; a tenant never collapses to a blank or sentinel value that a real tenant could equal.
- **Identity construction is deterministic.** A key is made from those values alone, never from the time or the
  Operation's own id, so a repeat that carries the same values always finds the same key, and contexts that differ in
  any of them never share one.

## Amendment (2026-10-08, Wave 2 final structural review: the trusted peer's continuation is restored as trusted)

The decision above stands, and so do the #57 and #48 amendments. The #48 amendment records that Web built a trusted
peer's context with `new OperationContext(actor, tenant, correlationId, causationId)`, the public constructor that the
#57 amendment made untrusted, so the permission check authorized the continued Actor again: a peer's continuation into
a command that declares a permission was refused unless this Host's permission source granted it to that Actor for the
peer's request. The #57 amendment's requirement that Web call `RestoreTrusted` was not met; the final structural review
of Wave 2 found it (finding R1 of both reviewers).

- **Web restores the peer's context as trusted.** `OperationContextHttpHeaders.Continued` returns
  `OperationContext.RestoreTrusted(actor, tenant, correlationId, causationId)`, the second caller of that member beside
  Messaging.Wolverine's delivery step. It is reached only after `HttpCallerContext` found an authenticated identity
  naming a configured peer, so no other caller's headers reach it, and Web adds no other way to set the mark.
- Proven by `TrustedPeerTests.A_trusted_peer_continues_into_a_permission_protected_command_its_actor_holds_no_permission_for_and_an_untrusted_caller_is_refused`,
  red with the public constructor back (the peer was refused with 403), and by
  `TrustedRestoreTests.The_packages_cqrs_opens_to_use_no_internal_member_of_it_but_the_trusted_restore`, which now
  requires Web's call beside delivery's and went red the same way.

## Amendment (2026-10-16, ticket #116: the tenant requirement runs for a trusted continuation)

The decision above stands, and so do G.6 and the #57 amendment: a trusted continuation is not authorized again as its
Actor, and the permission check stands aside for it. Spec #115 ("Multi-tenancy", its second bullet) makes one exception,
deliberately: in a tenant-scoped Module the tenant requirement "runs for continuations too".

- **The tenant requirement.** `AddPouryaKitTenancy` registers the internal `TenantRequirement<T>` as an `IAuthorizer<T>`
  for every command and query of a tenant-scoped Module, or of an application without Modules that declares it on its
  own services. It refuses an Operation whose context has no tenant with `TenantErrors.Required` (`Tenant.Required`,
  forbidden), and it does not read `IsTrustedContinuation`. It checks the tenant the continuation carries, not its Actor's
  authority, so it is no re-authorization of the kind G.6 rules out: a continuation runs with the tenant its causing
  Operation had, or not at all, because a tenant-scoped Module's data belongs to tenants and no authority an earlier
  Operation held covers work under none.
- **What did not change.** The trust mark, `Continue()` and `RestoreTrusted` are as the #57 and #48 amendments record. The
  permission check still lets a trusted continuation through, and an application's own authorizer still decides for
  itself.
- Proven by `TenancyTests.A_trusted_continuation_into_a_tenant_scoped_module_runs_for_its_tenant_and_is_refused_without_one`,
  red with the requirement letting a trusted continuation through ([acceptance](../acceptance.md#tenant-scoped-modules-ticket-116)).

## Amendment (2026-10-17, ticket #119: Web's codec and convention are public for another transport)

The decision above stands, and so do the #57, #48 and #116 amendments. Spec #115 ("gRPC") reuses Web's one codec and its
begin-the-Operation convention instead of a second of each, so ticket #119 makes two members public and nothing else.

- **The codec.** `OperationContextHttpHeaders`, named internal in the #48 amendment, is public with one public member,
  `Pairs(context)`: the header names and values the outgoing handler writes, each value checked by the Kit's rule before
  any pair is returned. The handler writes those pairs. Reading a peer's headers (`Continued`) stays internal, so the
  restore as trusted is reached only as the #48 amendment's correction records.
- **The convention.** `BeginOperationPerRequest()` on a `RouteGroupBuilder` is the step `MapPouryaKitModuleEndpoints`
  applies to each Module's group. A root group outside every Module's applies it the same way, and its endpoints get the
  same context, a trusted peer's continuation included; the trust rule and the header names are unchanged.
- Proven by `WebConventionTests` ([acceptance](../acceptance.md#web-conventions-for-other-transports-ticket-119)).

## Amendment (2026-10-18, ticket #133: the context records the Notification its delivery delivers)

The decision above stands, and so do the #57, #48, #116 and #119 amendments. G.2 allows publishing an Integration Event
and pushing a realtime event only in a Notification Handler's delivery, and `PouryaKit.Realtime` references no
`PouryaKit.Messaging.Wolverine`, so the fact moves to the context both publishers can read.

- **`OperationContext.Notification`** is the Domain Event a Notification Handler's delivery delivers, null in every other
  context. Only `RestoreTrusted` sets it, from an optional argument the delivery step passes for a Notification, so Cqrs
  still opens its internals for that restore alone; `Continue()` and a trusted peer's restore leave it null.
- **One fact.** The Integration Event publisher's own record of the Domain Event it follows is gone: it and the realtime
  publisher read `Notification`, so a command, a trusted peer's request or a command sent with `Continue()` publishes
  and pushes nothing.
- Proven by `RealtimeTests.A_trusted_peers_command_and_a_command_continued_through_the_gateway_cannot_push`, red with the
  push let through for any trusted continuation in a Unit of Work, and by the Integration Event tests of the publisher's
  refusals ([acceptance](../acceptance.md#realtime-ticket-133)).
