namespace PouryaKit.ArchitectureTests;

// ADR 0002 and story 85: no package in a Kit package's closure is reciprocal or commercially licensed. The closure is
// checked whole, analyzers and source generators included, which is stricter than the runtime closure alone.
public sealed class LicenceTests
{
    // Permissive licences only, as SPDX identifiers; the list is recorded in docs/provenance/README.md.
    private static readonly HashSet<string> Permissive = ["MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "PostgreSQL"];

    [Fact]
    public void Every_package_in_every_kit_closure_carries_a_permissive_licence_expression()
    {
        var packages = RestoredPackage.All.SelectMany(package => package.Packages).DistinctBy(package => (package.Id, package.Version)).ToList();

        Assert.Contains(packages, package => package.Id == "WolverineFx");
        Assert.Empty(Violations(packages));
    }

    [Fact]
    public void A_package_without_a_licence_expression_or_with_another_licence_is_rejected_by_name()
    {
        PackageLicence[] packages =
        [
            new("Unlabelled", "1.0.0", null),
            new("Reciprocal", "2.0.0", "RPL-1.5"),
            new("EitherOr", "3.0.0", "MIT OR GPL-3.0-only"),
            new("Both", "4.0.0", "MIT AND GPL-3.0-only"),
            new("Grouped", "5.0.0", "(MIT OR Apache-2.0)"),
            new("Plain", "6.0.0", "Apache-2.0"),
        ];

        Assert.Equal(
            [
                "Unlabelled 1.0.0 declares no licence expression.",
                "Reciprocal 2.0.0 is licensed RPL-1.5, which is not on the permissive list.",
                "Both 4.0.0 is licensed MIT AND GPL-3.0-only, which is not on the permissive list.",
                "Grouped 5.0.0 is licensed (MIT OR Apache-2.0), which is not on the permissive list.",
            ],
            Violations(packages)
        );
    }

    // An expression passes when it is one listed licence, a choice ("OR") that offers one, or a conjunction ("AND") of
    // listed licences only. Anything else, parentheses and exceptions included, needs a person to read it.
    internal static IEnumerable<string> Violations(IEnumerable<PackageLicence> packages) =>
        from package in packages
        let expression = package.Expression
        where expression is null || !IsPermissive(expression)
        select expression is null
            ? $"{package.Id} {package.Version} declares no licence expression."
            : $"{package.Id} {package.Version} is licensed {expression}, which is not on the permissive list.";

    private static bool IsPermissive(string expression) =>
        expression.Split(" OR ").Any(choice => choice.Split(" AND ").All(Permissive.Contains));
}
