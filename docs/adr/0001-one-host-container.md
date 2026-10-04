# One host container, not a container per module

Modules share the host's single dependency-injection container; isolation comes from separate
assemblies, `internal` types, a DbContext and schema per module, architecture tests, and operation
scopes opened by the gateway. A private container per module (as Cybernod does) gives harder
isolation, but Wolverine supports only the built-in container, so private containers would force the
kit to own provider lifecycle, shared-singleton plumbing and a hand-wired outbox bridge.

## Consequences

A module can resolve another module's *public* registered services; architecture tests must forbid it.
