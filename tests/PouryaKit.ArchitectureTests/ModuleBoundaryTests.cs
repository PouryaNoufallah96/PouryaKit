using System.Reflection;
using Catalog.Products.Events.Notifications;
using Offending;
using Offending.Contracts;
using PouryaKit.Reporting.Module;

namespace PouryaKit.ArchitectureTests;

// Rules 1 and 6: what one Module can see of another. Each rule judges the Proving Sample, then the offending fixture.
public sealed class ModuleBoundaryTests
{
    private static readonly Assembly OffendingModuleAssembly = typeof(OffendingModule).Assembly;

    // A type the Host implements for its Module is public so the Host can implement it; it is listed by name, so any
    // other public type in a Module fails.
    private static readonly Dictionary<string, IReadOnlySet<Type>> HostPorts = new()
    {
        ["Catalog"] = new HashSet<Type> { typeof(INewProductAnnouncer), typeof(NewProductAnnouncement) },
        ["PouryaKit.Reporting.Module"] = new HashSet<Type>
        {
            typeof(IReportRequestAuthorizer),
            typeof(IReportLayouts),
            typeof(ReportRequest),
        },
    };

    [Fact]
    public void The_host_composes_the_three_sample_modules_and_the_report_module_and_catalog_has_contracts()
    {
        Assert.Equal(["Billing", "Catalog", "Inventory", "PouryaKit.Reporting.Module"], Sample.Modules.Select(module => module.GetName().Name));
        Assert.Equal(["Catalog.Contracts"], Sample.Contracts.Select(contracts => contracts.GetName().Name));
    }

    private static readonly HashSet<string> ModuleNames = [.. Sample.Modules.Select(module => module.GetName().Name!), "Offending"];

    [Fact]
    public void No_module_references_another_modules_implementation()
    {
        Assert.Empty(
            Sample.Modules.SelectMany(module => ModuleRules.ReferencesToOtherModules(module, Sample.RestoredOf(module), ModuleNames))
        );
        // The restored closure is read: Inventory reaches Catalog's Contracts and the Kit through it.
        Assert.Superset(
            new HashSet<string> { "Catalog.Contracts", "PouryaKit.Cqrs" },
            new HashSet<string>(Sample.RestoredOf(Assert.Single(Sample.Modules, module => module.GetName().Name == "Inventory")).Projects)
        );
    }

    [Fact]
    public void A_module_whose_project_references_another_modules_implementation_is_rejected_though_no_code_uses_it()
    {
        Assert.DoesNotContain(OffendingModuleAssembly.GetReferencedAssemblies(), reference => reference.Name == "Catalog");

        var violations = ModuleRules.ReferencesToOtherModules(OffendingModuleAssembly, Restored("Offending"), ModuleNames);

        Assert.Equal(
            ["Offending references Catalog, the implementation of another Module; reach it through Catalog.Contracts."],
            violations
        );
    }

    [Fact]
    public void Contracts_reference_nothing_from_the_kit_and_no_module()
    {
        Assert.Empty(
            Sample.Contracts.SelectMany(contracts => ModuleRules.ReferencesOfContracts(contracts, Sample.RestoredOf(contracts), ModuleNames))
        );
    }

    [Fact]
    public void Contracts_whose_project_references_the_kit_are_rejected_though_no_code_uses_it()
    {
        var contracts = typeof(OffendingCreatedV1).Assembly;
        Assert.DoesNotContain(contracts.GetReferencedAssemblies(), reference => reference.Name == "PouryaKit.Domain");

        var violations = ModuleRules.ReferencesOfContracts(contracts, Restored("Offending.Contracts"), ModuleNames);

        Assert.Equal(["Offending.Contracts references PouryaKit.Domain; Contracts reference nothing from the Kit and no Module."], violations);
    }

    private static RestoredPackage Restored(string fixture) =>
        RestoredPackage.Of(Path.Combine(Repository.Root, "tests", "PouryaKit.ArchitectureTests", fixture, $"{fixture}.csproj"));

    [Fact]
    public void A_modules_only_public_types_are_its_composition_root_and_what_its_host_implements_for_it()
    {
        Assert.Empty(
            Sample.Modules.SelectMany(module =>
                ModuleRules.PublicTypesOf(module, HostPorts.GetValueOrDefault(module.GetName().Name!) ?? new HashSet<Type>())
            )
        );
    }

    [Fact]
    public void A_public_type_that_is_not_a_migration_is_rejected()
    {
        var violations = ModuleRules.PublicTypesOf(OffendingModuleAssembly, new HashSet<Type>());

        // The public migration and the Composition Root are not among them.
        Assert.Equal(
            [typeof(ExposedQuery).FullName, typeof(ExposedQueryHandler).FullName],
            violations.Order(StringComparer.Ordinal)
        );
    }
}
