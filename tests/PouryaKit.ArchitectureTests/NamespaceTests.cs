extern alias OffendingCore;

using System.CodeDom.Compiler;
using System.Reflection;
using System.Runtime.CompilerServices;
using Offending;

namespace PouryaKit.ArchitectureTests;

// ADR 0010: a Kit package has one namespace, its own name, whatever folder a file sits in. An editor gives a new file
// its folder's namespace, so this rule catches the one way the policy breaks without a word.
public sealed class NamespaceTests
{
    [Fact]
    public void Every_type_of_a_kit_package_is_in_the_namespace_named_after_it()
    {
        Assert.Empty(Sample.KitPackages.SelectMany(Violations));
    }

    [Fact]
    public void A_type_outside_its_packages_namespace_is_rejected()
    {
        // A sub-namespace, the editor's folder default: every other type of Offending is in Offending, and its OpenAPI
        // generator's output is marked.
        Assert.Equal(
            ["Offending.Stray.StrayType is not in Offending, the one namespace of its package."],
            Violations(typeof(OffendingModule).Assembly)
        );

        // An unrelated namespace: Offending.Core compiles Core's sources and TextHelpers, all in PouryaKit.Core, into
        // an assembly named Offending.Core. Its collection-expression type is generated, so it is not among them.
        var grown = typeof(OffendingCore::PouryaKit.Core.TextHelpers).Assembly;

        Assert.Equal(
            [
                "PouryaKit.Core.ImplementingTypes is not in Offending.Core, the one namespace of its package.",
                "PouryaKit.Core.PredicateBuilder is not in Offending.Core, the one namespace of its package.",
                "PouryaKit.Core.SynchronizationContextScope is not in Offending.Core, the one namespace of its package.",
                "PouryaKit.Core.TextHelpers is not in Offending.Core, the one namespace of its package.",
            ],
            Violations(grown).Order(StringComparer.Ordinal)
        );
    }

    // Only top-level types are judged: a nested type is in its declaring type's namespace, and a generated type's
    // nested types carry no marker of their own. A generated definition is left out by its marker, not by its name.
    // Today these are the compiler's <PrivateImplementationDetails>, anonymous types and collection-expression types in
    // the global namespace, the GeneratedRegex output in System.Text.RegularExpressions.Generated (Messaging), and the
    // OpenAPI XML comment generator's output in Microsoft.AspNetCore.OpenApi.Generated and
    // System.Runtime.CompilerServices (Web.OpenApi). The one exception is JasperFx's event model manifest, which
    // JasperFx.SourceGenerator 2.91.0 (WolverineFx 6.52.0's) writes into every assembly that sees JasperFx.Events,
    // marked only by an auto-generated comment, so it is left out by its full name.
    private static IEnumerable<string> Violations(Assembly package) =>
        from type in package.GetTypes()
        where !type.IsNested && !IsGenerated(type) && type.Namespace != package.GetName().Name
        select $"{type.FullName} is not in {package.GetName().Name}, the one namespace of its package.";

    private static bool IsGenerated(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        || type.IsDefined(typeof(GeneratedCodeAttribute), inherit: false)
        || type.FullName == "JasperFx.Generated.DiscoveredEventModels";
}
