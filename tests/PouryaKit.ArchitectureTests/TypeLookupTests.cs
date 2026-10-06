using System.Reflection;
using Offending;

namespace PouryaKit.ArchitectureTests;

// Ticket #55: one implementing-type lookup. Only PouryaKit.Core's ImplementingTypes lists the types of an assembly; every
// other Kit package asks it.
public sealed class TypeLookupTests
{
    private static readonly HashSet<string> AssemblyListings =
    [
        nameof(Assembly.GetTypes),
        $"get_{nameof(Assembly.DefinedTypes)}",
        $"get_{nameof(Assembly.ExportedTypes)}",
        nameof(Assembly.GetExportedTypes),
        $"get_{nameof(Assembly.Modules)}",
        nameof(Assembly.GetModules),
    ];

    private static readonly HashSet<string> ModuleListings = [nameof(Module.GetTypes), nameof(Module.FindTypes)];

    [Fact]
    public void Only_core_lists_the_types_of_an_assembly()
    {
        Assert.Empty(Sample.KitPackages.Where(package => package.GetName().Name != "PouryaKit.Core").SelectMany(TypeListings));
        // Core itself is where the types are listed, and the rule sees it.
        var core = Assert.Single(Sample.KitPackages, package => package.GetName().Name == "PouryaKit.Core");
        Assert.Equal(["PouryaKit.Core.ImplementingTypes calls Assembly.GetTypes."], TypeListings(core));
    }

    [Fact]
    public void Code_that_lists_the_types_of_an_assembly_itself_is_rejected()
    {
        Assert.Equal(
            [
                "Offending.OwnTypeLookup calls Assembly.GetExportedTypes.",
                "Offending.OwnTypeLookup calls Assembly.GetModules.",
                "Offending.OwnTypeLookup calls Assembly.GetTypes.",
                "Offending.OwnTypeLookup calls Assembly.get_DefinedTypes.",
                "Offending.OwnTypeLookup calls Assembly.get_ExportedTypes.",
                "Offending.OwnTypeLookup calls Assembly.get_Modules.",
                "Offending.OwnTypeLookup calls Module.FindTypes.",
                "Offending.OwnTypeLookup calls Module.GetTypes.",
            ],
            TypeListings(typeof(OffendingModule).Assembly).Order(StringComparer.Ordinal)
        );
    }

    private static IEnumerable<string> TypeListings(Assembly assembly) =>
        Il.CallsIn(assembly)
            .Where(call =>
                call.Callee.DeclaringType is { } declaring
                && (
                    (declaring.IsAssignableTo(typeof(Assembly)) && AssemblyListings.Contains(call.Callee.Name))
                    || (declaring.IsAssignableTo(typeof(Module)) && ModuleListings.Contains(call.Callee.Name))
                )
            )
            .Select(call => $"{call.Owner.FullName} calls {call.Callee.DeclaringType!.Name}.{call.Callee.Name}.")
            .Distinct();
}
