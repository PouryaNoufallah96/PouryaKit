using Offending;

namespace PouryaKit.ArchitectureTests;

// Spec #33 and ADR 0002: applications register no pipeline steps, so no application implements a contract the Kit fills
// for its pipeline. The rule judges the Proving Sample's application code, its two Hosts and what they reference, then
// the offending fixture, and the Kit's packages, where each contract is implemented, so a rule that reads nothing fails.
public sealed class PipelineContractTests
{
    [Fact]
    public void No_module_or_host_implements_a_pipeline_contract_and_the_kit_implements_each()
    {
        Assert.Equal(
            ["Billing", "Catalog", "Catalog.Contracts", "Inventory", "Inventory.Host", "ModularMonolith.Host", "ModularMonolith.ServiceDefaults"],
            Sample.Application.Select(assembly => assembly.GetName().Name)
        );
        Assert.Empty(Sample.Application.SelectMany(ModuleRules.ImplementsPipelineContracts));
        Assert.Equal(
            [
                "PouryaKit.Caching.HybridQueryCache implements IQueryCache, a pipeline contract only the Kit implements (PouryaKit.Caching.dll).",
                "PouryaKit.Cqrs.Dispatcher implements IDispatcher, a pipeline contract only the Kit implements (PouryaKit.Cqrs.dll).",
                "PouryaKit.Messaging.Wolverine.EFCore.EfReceipts implements IReceipts, a pipeline contract only the Kit implements (PouryaKit.Messaging.Wolverine.EFCore.dll).",
                "PouryaKit.Messaging.Wolverine.EFCore.PublishedIntegrationEvents implements IIntegrationEventOutbox, a pipeline contract only the Kit implements (PouryaKit.Messaging.Wolverine.EFCore.dll).",
                "PouryaKit.Messaging.Wolverine.EFCore.ScheduledDelayedCommands implements IDelayedCommandOutbox, a pipeline contract only the Kit implements (PouryaKit.Messaging.Wolverine.EFCore.dll).",
                "PouryaKit.Messaging.Wolverine.EFCore.WolverineOutbox implements INotificationOutbox, a pipeline contract only the Kit implements (PouryaKit.Messaging.Wolverine.EFCore.dll).",
                "PouryaKit.Messaging.Wolverine.EFCore.WolverineOutbox implements IUnitOfWorkOutbox, a pipeline contract only the Kit implements (PouryaKit.Messaging.Wolverine.EFCore.dll).",
                "PouryaKit.Messaging.Wolverine.SnapshotMappers implements ISnapshotMappers, a pipeline contract only the Kit implements (PouryaKit.Messaging.Wolverine.dll).",
                "PouryaKit.Modularity.GatewayProcessor`1 implements IGatewayProcessor, a pipeline contract only the Kit implements (PouryaKit.Modularity.dll).",
                "PouryaKit.Observability.ObservedDispatcher implements IDispatcher, a pipeline contract only the Kit implements (PouryaKit.Observability.dll).",
                "PouryaKit.Persistence.EFCore.EfIdempotencyStore implements IIdempotencyStore, a pipeline contract only the Kit implements (PouryaKit.Persistence.EFCore.dll).",
                "PouryaKit.Persistence.EFCore.EfUnitOfWork implements IReadingStateGuard, a pipeline contract only the Kit implements (PouryaKit.Persistence.EFCore.dll).",
                "PouryaKit.Persistence.EFCore.EfUnitOfWork implements IUnitOfWork, a pipeline contract only the Kit implements (PouryaKit.Persistence.EFCore.dll).",
                "PouryaKit.Validation.FluentValidationStep`1 implements IValidationStep, a pipeline contract only the Kit implements (PouryaKit.Validation.dll).",
            ],
            Sample.KitPackages.SelectMany(ModuleRules.ImplementsPipelineContracts).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void An_application_type_that_implements_a_pipeline_contract_is_rejected()
    {
        Assert.Equal(
            ["Offending.OwnQueryCache implements IQueryCache, a pipeline contract only the Kit implements (Offending.dll)."],
            ModuleRules.ImplementsPipelineContracts(typeof(OffendingModule).Assembly)
        );
    }
}
