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

## Amendment (2026-10-07, ticket #61: a Module's Features live in its definition's assembly)

The decision above stands. The owner decided on 2026-10-07 (question A of the Kit API review) that isolation by
assembly is also the convention the Kit infers from, so a Composition Root no longer names its own Module or assembly.

- **A Module's Features live in the assembly of its definition**, the class that implements `IModuleDefinition`. A Kit
  registration that scans Features on a Module's builder with no assembly scans that one: `AddPouryaKitCqrs()`,
  `AddPouryaKitValidation()`, `AddPouryaKitPermissions()`, `AddPouryaKitDapper(connectionName)`,
  `AddPouryaKitNotificationHandlers()` and `AddPouryaKitEndpoints(prefix)`. Cybernod finds a Module by its definition's
  assembly too (`cybernod@bae8ac3 src/BuildingBlocks/BuildingBlocks.Web/Modules/ModuleRegistry.cs:38-41`); its static
  registry is not adopted.
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
