extern alias OffendingCore;

using System.Reflection;

namespace PouryaKit.ArchitectureTests;

// Plan I.3 and ticket #37: PouryaKit.Core holds three helpers and nothing else, so it never becomes the place unrelated
// helpers go. The rule judges Core itself, then a copy of Core's sources with a fourth public helper.
public sealed class CoreTests
{
    private static readonly string[] Helpers =
    [
        "PouryaKit.Core.ImplementingTypes",
        "PouryaKit.Core.PredicateBuilder",
        "PouryaKit.Core.SynchronizationContextScope",
    ];

    [Fact]
    public void Core_exposes_its_three_helpers_and_no_other_public_type()
    {
        var core = Assert.Single(Sample.KitPackages, package => package.GetName().Name == "PouryaKit.Core");

        Assert.Equal(Helpers, PublicTypes(core));
    }

    [Fact]
    public void A_fourth_public_type_in_core_is_rejected()
    {
        var grown = typeof(OffendingCore::PouryaKit.Core.TextHelpers).Assembly;

        Assert.Equal(
            ["PouryaKit.Core exposes PouryaKit.Core.TextHelpers, a public type beyond its three helpers."],
            Violations(grown)
        );
    }

    private static IEnumerable<string> Violations(Assembly core) =>
        from name in PublicTypes(core)
        where !Helpers.Contains(name)
        select $"PouryaKit.Core exposes {name}, a public type beyond its three helpers.";

    // A type a consumer can name. An extension block compiles to public nested types with unspeakable names, which are
    // part of the helper that declares the block.
    private static IEnumerable<string> PublicTypes(Assembly core) =>
        core.GetExportedTypes().Where(type => !type.Name.StartsWith('<')).Select(type => type.FullName!).Order(StringComparer.Ordinal);
}
