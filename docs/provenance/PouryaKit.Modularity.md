# PouryaKit.Modularity provenance

Adopted implementations in `PouryaKit.Modularity`. Format and rules: [README](README.md).

## Package dependency

`Microsoft.Extensions.Hosting.Abstractions` 10.0.12, licence `MIT` as published on nuget.org (licence expression in the package's nuspec; checked 2026-10-05). It is the package's only third-party reference (plan section C).

## Composition Root and Gateway

- **Kit files:** `src/PouryaKit.Modularity/IModuleDefinition.cs`, `src/PouryaKit.Modularity/IGatewayProcessor.cs`, `src/PouryaKit.Modularity/GatewayProcessor.cs`, `src/PouryaKit.Modularity/ModuleBuilder.cs`, `src/PouryaKit.Modularity/ModulesBuilder.cs`, `src/PouryaKit.Modularity/ModularityHostApplicationBuilderExtensions.cs`, `src/PouryaKit.Modularity/HostModules.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Web/Modules/IModuleDefinition.cs`, `src/BuildingBlocks/BuildingBlocks.Web/Modules/IGatewayProcessor.cs`, `src/BuildingBlocks/BuildingBlocks.Web/Modules/GatewayProcessor.cs`, `src/BuildingBlocks/BuildingBlocks.Web/Modules/Extensions/ModuleExtensions.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #8, #13
- **Intentional differences:**
  - One Host container (ADR 0001): no private container per Module, no `CompositionRootRegistry`, `ModuleRegistry` or other static registry, and no copy of the service collection. A Module's Composition Root registers on a `ModuleBuilder` that writes into the Host's services.
  - The Gateway opens a scope of the Host container per call, tags it with its Module, sets the `OperationContext` its caller passes (a required argument, ADR 0007), and dispatches through the Kit's `IDispatcher`. It offers only `SendAsync` for commands and `QueryAsync` for queries; the mapper, event-bus, scheduling and streaming members are dropped.
  - Wiring mistakes fail early instead of falling back: a Module added twice to a Host throws while the Host is composed, and a command or query sent through the Gateway of a Module that does not own it throws before any scope opens. The Modules and the operations each owns are recorded in one object kept in the Host's service collection, not in a static, as the Composition Root registers its handlers on its `ModuleBuilder`.
  - The Unit of Work a Composition Root registers is keyed by its Module, and the one unkeyed `IUnitOfWork` resolves the key from the scope's tag, so each Module's Operations commit through its own DbContext in one container.
  - A Module is a definition type with one `Configure(ModuleBuilder)` member, added by type through `AddPouryaKitModules`; there is no assembly scan for Modules and no endpoint or middleware hook yet (ticket #14).
