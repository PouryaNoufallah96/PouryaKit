# One host container, not a container per module

Modules share the host's single dependency-injection container; isolation comes from separate
assemblies, `internal` types, a DbContext and schema per module, architecture tests, and operation
scopes opened by the gateway. A private container per module (as Cybernod does) gives harder
isolation, but Wolverine supports only the built-in container, so private containers would force the
kit to own provider lifecycle, shared-singleton plumbing and a hand-wired outbox bridge.

## Consequences

A module can resolve another module's *public* registered services; architecture tests must forbid it.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands. In one container a handler is a registration any Module could add or remove, so tickets #13
and #19 made each Module's ownership a composition rule (`src/PouryaKit.Modularity/HostModules.cs`). Loosening one
would let an Operation run in another Module's Unit of Work without an error.

- **A command or query belongs to the Module that registers its handler.** A second Module that registers, replaces
  or removes that handler fails composition, naming both Modules
  (`ModuleWiringTests.A_module_that_registers_or_removes_another_modules_handler_fails_startup_naming_both`), and a
  Gateway refuses an operation another Module owns
  (`GatewayTests.A_command_sent_through_another_modules_gateway_is_refused_naming_both_modules`).
- **A Module that handles a command registers its Unit of Work**, or composition fails; a Module with queries only
  needs none
  (`ModuleWiringTests.A_module_that_handles_a_command_without_a_unit_of_work_fails_startup_naming_it_and_a_query_only_module_needs_none`).
  Ticket #13 had left this to the first command's run; ticket #19 moved it to composition.
- **A port the Host implements stays public.** Beside the `internal` types above, a Module's Composition Root is
  public, and so is a port its Host implements for it, such as Catalog's `INewProductAnnouncer`, so the Host can
  implement it. The architecture test lists each port by name, so no other public type passes as one
  (`ModuleBoundaryTests.A_modules_only_public_types_are_its_composition_root_and_what_its_host_implements_for_it`).
