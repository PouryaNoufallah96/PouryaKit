# One host container, not a container per module

Modules share the host's single dependency-injection container; isolation comes from separate
assemblies, `internal` types, a DbContext and schema per module, architecture tests, and operation
scopes opened by the gateway. A private container per module (as Cybernod does) gives harder
isolation, but Wolverine supports only the built-in container, so private containers would force the
kit to own provider lifecycle, shared-singleton plumbing and a hand-wired outbox bridge.

## Consequences

A module can resolve another module's *public* registered services; architecture tests must forbid it.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands. In one container a handler is a registration any Module could add or remove, so each
Module's ownership is a composition rule (`src/PouryaKit.Modularity/HostModules.cs`). Loosening one
would let an Operation run in another Module's Unit of Work without an error.

- **A command or query belongs to the Module that registers its handler.** A second Module that registers, replaces
  or removes that handler fails composition, naming both Modules
  (`ModuleWiringTests.A_module_that_registers_or_removes_another_modules_handler_fails_startup_naming_both`), and a
  Gateway refuses an operation another Module owns
  (`GatewayTests.A_command_sent_through_another_modules_gateway_is_refused_naming_both_modules`).
- **A Module that handles a command registers its Unit of Work**, or composition fails; a Module with queries only
  needs none
  (`ModuleWiringTests.A_module_that_handles_a_command_without_a_unit_of_work_fails_startup_naming_it_and_a_query_only_module_needs_none`).
- **A port the Host implements stays public.** Beside the `internal` types above, a Module's Composition Root is
  public, and so is a port its Host implements for it, such as Catalog's `INewProductAnnouncer`, so the Host can
  implement it. The architecture test lists each port by name, so no other public type passes as one
  (`ModuleBoundaryTests.A_modules_only_public_types_are_its_composition_root_and_what_its_host_implements_for_it`).

## Amendment (2026-10-07: a Module's Features live in its definition's assembly)

The decision above stands. Isolation by assembly is also the convention the Kit infers from, so a Composition Root
no longer names its own Module or assembly.

- **A Module's Features live in the assembly of its definition**, the class that implements `IModuleDefinition`. A Kit
  registration that scans Features on a Module's builder with no assembly scans that one: `AddPouryaKitCqrs()`,
  `AddPouryaKitValidation()`, `AddPouryaKitPermissions()`, `AddPouryaKitDapper(connectionName)`,
  `AddPouryaKitNotificationHandlers()` and `AddPouryaKitEndpoints(prefix)`.
- **Features elsewhere are named.** Named assemblies replace the inferred one and keep their order, which is the order an
  Operation's authorizers run in. On the Host's own services nothing is inferred: an application without Modules names
  its assembly, and `AddPouryaKitCqrs()` there registers the dispatcher alone.
- **One seam.** `ModuleBuilder` implements Cqrs's `IModuleServices`, which gives a registration the Module's definition
  type; its assembly is derived from that type, never stored apart. Packages detect it at run time
  (`services is IModuleServices`), so a helper typed as `IServiceCollection` keeps the Module, and every package resolves
  the Module and the assemblies through the seam's one helper. Each Module and the Feature assemblies its registrations
  scanned are recorded Host-locally and read through `HostComposition` when the Host's container is built; every Module
  `AddPouryaKitModules` adds counts as composed, whatever its Composition Root registers.
- Proven by the Proving Sample, whose three Composition Roots name neither their Module nor their assembly, and by
  `CompositionCheckTests.The_host_composition_names_each_module_with_the_feature_assembly_its_builder_inferred`.

## Amendment (2026-10-18: a Saga is the one public handler type)

The decision above stands. The one exception, a Saga, is recorded in
[ADR 0013](0013-a-saga-step-runs-outside-every-operation.md).

- **A saga's type is public.** Its state and handler type is a Wolverine `Saga` whose chain Wolverine generates, and
  generated code names it, so it is public, as are the Kit's input type of each event it takes and its timeout
  messages. A Module that declares a saga has that type beside its Composition Root and the ports its Host implements
  for it; the architecture test that lists a Module's public types names it.
- **A step reaches no Kit scoped service while the Host keeps Wolverine's default service location policy.** Under
  `ServiceLocationPolicy.NotAllowed`, Wolverine's default, a saga's generated chain cannot take the Kit's `Operation` or a
  Kit service whose implementation is internal (ADR 0013, item 8), so a Module's internals stay out of a step's reach. A
  Host that allows service location gives a step both; ADR 0013 records a test that fails if the default changes, and
  `PouryaKit.Messaging.Wolverine.Sagas` keeps the policy for a Host with a saga.
- **Isolation by assembly holds.** A saga, its timeouts and its declaration live in its Module's assembly, and another
  Module reaches it only through Integration Events.
