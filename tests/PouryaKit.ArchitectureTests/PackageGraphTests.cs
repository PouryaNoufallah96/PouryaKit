using System.Xml.Linq;

namespace PouryaKit.ArchitectureTests;

// Story 84 and row 17 of plan B.1: every Kit package's restored closure, transitive arrivals included, against plan
// section C as amended.
public sealed class PackageGraphTests
{
    public static TheoryData<string> KitPackages() => [.. RestoredPackage.All.Select(package => package.Name)];

    [Theory]
    [MemberData(nameof(KitPackages))]
    public void A_kit_packages_references_and_closure_are_those_section_c_allows(string name)
    {
        Assert.Empty(PackageGraph.Violations(Assert.Single(RestoredPackage.All, package => package.Name == name)));
    }

    [Fact]
    public void Every_kit_package_is_checked_and_closures_are_read_transitively()
    {
        Assert.Equal(17, RestoredPackage.All.Count);
        // EF Core arrives in Postgres only through its provider, and Domain in Web only through Cqrs.
        Assert.Contains(Restored("PouryaKit.Persistence.Postgres").Packages, arrived => arrived.Id == "Microsoft.EntityFrameworkCore");
        Assert.Contains("PouryaKit.Domain", Restored("PouryaKit.Web").Projects);
    }

    [Fact]
    public void A_closure_with_a_package_section_c_keeps_out_is_rejected()
    {
        var dapper = Restored("PouryaKit.Persistence.Dapper");
        var wolverine = Restored("PouryaKit.Messaging.Wolverine");
        var web = Restored("PouryaKit.Web");
        var openApi = Restored("PouryaKit.Web.OpenApi");
        var messaging = Restored("PouryaKit.Messaging");

        Assert.Equal(
            ["PouryaKit.Persistence.Dapper's closure holds Microsoft.EntityFrameworkCore.Relational, which section C keeps out of it."],
            PackageGraph.Violations(dapper with { Packages = [.. dapper.Packages, Arrived("Microsoft.EntityFrameworkCore.Relational")] })
        );
        Assert.Equal(
            [
                "PouryaKit.Messaging.Wolverine's closure holds Npgsql, which section C keeps out of it.",
                "PouryaKit.Messaging.Wolverine's closure holds WolverineFx.RuntimeCompilation, which section C keeps out of it.",
            ],
            PackageGraph.Violations(
                wolverine with { Packages = [.. wolverine.Packages, Arrived("Npgsql"), Arrived("WolverineFx.RuntimeCompilation")] }
            )
        );
        Assert.Equal(
            ["PouryaKit.Web's closure holds PouryaKit.Modularity, which section C keeps out of it."],
            PackageGraph.Violations(web with { Projects = new HashSet<string>([.. web.Projects, "PouryaKit.Modularity"]) })
        );
        Assert.Equal(
            ["PouryaKit.Web's closure holds Aspire.Hosting, which section C keeps out of it."],
            PackageGraph.Violations(web with { Packages = [.. web.Packages, Arrived("Aspire.Hosting")] })
        );
        Assert.Equal(
            ["PouryaKit.Web's closure holds Asp.Versioning.Http, which section C keeps out of it."],
            PackageGraph.Violations(web with { Packages = [.. web.Packages, Arrived("Asp.Versioning.Http")] })
        );
        Assert.Equal(
            ["PouryaKit.Web.OpenApi's closure holds Swashbuckle.AspNetCore.SwaggerGen, which section C keeps out of it."],
            PackageGraph.Violations(openApi with { Packages = [.. openApi.Packages, Arrived("Swashbuckle.AspNetCore.SwaggerGen")] })
        );
        Assert.Equal(
            ["PouryaKit.Messaging's closure holds the packages [WolverineFx]; it must hold none."],
            PackageGraph.Violations(messaging with { Packages = [Arrived("WolverineFx")] })
        );
    }

    [Fact]
    public void A_direct_reference_section_c_does_not_list_is_rejected()
    {
        var cqrs = Restored("PouryaKit.Cqrs");

        Assert.Equal(
            [
                "PouryaKit.Cqrs references the packages [MediatR, Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions]; section C allows [Microsoft.Extensions.DependencyInjection.Abstractions, Microsoft.Extensions.Logging.Abstractions].",
            ],
            PackageGraph.Violations(cqrs with { DirectPackages = new HashSet<string>([.. cqrs.DirectPackages, "MediatR"]) })
        );
    }

    [Fact]
    public void Produced_nuspecs_obey_the_same_reference_rules_as_source_projects()
    {
        var nuspec = XDocument.Parse("""
            <package><metadata><id>PouryaKit.Persistence.Dapper</id><dependencies><group targetFramework="net10.0">
              <dependency id="PouryaKit.Core" version="0.1.0-preview.1" />
              <dependency id="PouryaKit.Cqrs" version="0.1.0-preview.1" />
              <dependency id="Dapper" version="2.1.89" />
              <dependency id="Npgsql" version="10.0.3" />
              <dependency id="Microsoft.Extensions.Configuration.Abstractions" version="10.0.12" />
              <dependency id="Microsoft.Extensions.DependencyInjection.Abstractions" version="10.0.12" />
            </group></dependencies></metadata></package>
            """);
        Assert.Empty(PackageGraph.Violations(RestoredPackage.FromNuspec(nuspec)));

        nuspec.Descendants().Single(element => element.Name.LocalName == "group")
            .Add(new XElement("dependency", new XAttribute("id", "Microsoft.EntityFrameworkCore")));
        Assert.Contains(PackageGraph.Violations(RestoredPackage.FromNuspec(nuspec)),
            violation => violation.Contains("references the packages", StringComparison.Ordinal)
                && violation.Contains("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    private static RestoredPackage Restored(string name) => Assert.Single(RestoredPackage.All, package => package.Name == name);

    private static PackageLicence Arrived(string id) => new(id, "1.0.0", "MIT");
}
