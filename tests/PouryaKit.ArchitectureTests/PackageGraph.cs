namespace PouryaKit.ArchitectureTests;

/// <summary>
/// Plan section C, "Allowed references", as amended on 2026-10-05 and by G.4, as the checks a restored Kit package must
/// pass: its direct references are exactly the table's, and its full closure keeps out what the table keeps out.
/// </summary>
internal static class PackageGraph
{
    private sealed record Allowed(string[] Projects, string[] Packages, string[]? Frameworks = null);

    // The table names third-party packages informally; these are their ids. "EF Core Relational" is
    // Microsoft.EntityFrameworkCore.Relational, "Npgsql EF provider" Npgsql.EntityFrameworkCore.PostgreSQL, "xUnit v3"
    // xunit.v3.extensibility.core (the package for libraries) and "ASP.NET Core framework reference" the framework
    // Microsoft.AspNetCore.App. PouryaKit.Persistence has no row of its own: G.4 gives it Ardalis.Specification, over the
    // aggregates of Domain. Persistence.Dapper references Cqrs, not Domain, since its Page helper reads a Cqrs
    // PageRequest into a Page (I.3 Paging, ticket #35).
    private static readonly Dictionary<string, Allowed> Table = new()
    {
        ["PouryaKit.Core"] = new([], []),
        ["PouryaKit.Domain"] = new([], []),
        ["PouryaKit.Cqrs"] = new(
            ["PouryaKit.Core", "PouryaKit.Domain"],
            ["Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Logging.Abstractions"]
        ),
        ["PouryaKit.Validation"] = new(["PouryaKit.Cqrs"], ["FluentValidation"]),
        ["PouryaKit.Modularity"] = new(
            ["PouryaKit.Cqrs"],
            ["Microsoft.Extensions.Hosting.Abstractions", "Microsoft.Extensions.DependencyInjection"]
        ),
        ["PouryaKit.Persistence"] = new(["PouryaKit.Domain"], ["Ardalis.Specification"]),
        ["PouryaKit.Persistence.EFCore"] = new(
            ["PouryaKit.Core", "PouryaKit.Cqrs", "PouryaKit.Persistence"],
            ["Microsoft.EntityFrameworkCore.Relational", "Ardalis.Specification.EntityFrameworkCore"]
        ),
        ["PouryaKit.Persistence.Postgres"] = new(
            ["PouryaKit.Persistence.EFCore"],
            ["Npgsql.EntityFrameworkCore.PostgreSQL", "EFCore.NamingConventions"]
        ),
        ["PouryaKit.Persistence.Dapper"] = new(
            ["PouryaKit.Core", "PouryaKit.Cqrs"],
            ["Dapper", "Npgsql", "Microsoft.Extensions.Configuration.Abstractions", "Microsoft.Extensions.DependencyInjection.Abstractions"]
        ),
        ["PouryaKit.Messaging"] = new(["PouryaKit.Domain"], []),
        ["PouryaKit.Messaging.Wolverine"] = new(["PouryaKit.Core", "PouryaKit.Cqrs", "PouryaKit.Messaging"], ["WolverineFx"]),
        ["PouryaKit.Messaging.Wolverine.EFCore"] = new(
            ["PouryaKit.Messaging.Wolverine", "PouryaKit.Persistence.EFCore"],
            ["WolverineFx.EntityFrameworkCore", "WolverineFx.Postgresql"]
        ),
        ["PouryaKit.Security"] = new(["PouryaKit.Core", "PouryaKit.Cqrs"], []),
        ["PouryaKit.Web"] = new(
            ["PouryaKit.Core", "PouryaKit.Cqrs"],
            ["Microsoft.Extensions.Http.Resilience"],
            ["Microsoft.AspNetCore.App"]
        ),
        ["PouryaKit.Testing"] = new(
            ["PouryaKit.Modularity", "PouryaKit.Persistence.Postgres", "PouryaKit.Messaging.Wolverine.EFCore"],
            ["Testcontainers.PostgreSql", "Respawn", "xunit.v3.extensibility.core", "Microsoft.Extensions.TimeProvider.Testing"]
        ),
    };

    // What must never arrive in a package's closure, by package id prefix or project name. Every package also keeps
    // WolverineFx.RuntimeCompilation out: Roslyn at run time is the Host's choice (ADR 0004).
    private static readonly Dictionary<string, string[]> KeptOut = new()
    {
        ["PouryaKit.Persistence.Dapper"] = ["Microsoft.EntityFrameworkCore"],
        ["PouryaKit.Persistence.Postgres"] = ["WolverineFx", "JasperFx", "PouryaKit.Messaging"],
        // Outgoing HTTP resilience is the standard handler alone, usable without an AppHost or ServiceDefaults.
        ["PouryaKit.Web"] = ["PouryaKit.Modularity", "Aspire"],
        // Transport- and database-neutral: no EF Core, no PostgreSQL driver, no message store.
        ["PouryaKit.Messaging.Wolverine"] = ["Microsoft.EntityFrameworkCore", "Npgsql", "WolverineFx.RDBMS", "WolverineFx.Postgresql", "Weasel"],
    };

    // Core and Domain, and Messaging (Domain only), bring no package at all.
    private static readonly HashSet<string> NoPackages = ["PouryaKit.Core", "PouryaKit.Domain", "PouryaKit.Messaging"];

    public static IEnumerable<string> Violations(RestoredPackage package)
    {
        if (!Table.TryGetValue(package.Name, out var allowed))
        {
            yield return $"{package.Name} has no row in plan section C's table.";
            yield break;
        }

        foreach (var (kind, actual, expected) in new[]
        {
            ("projects", package.DirectProjects, allowed.Projects),
            ("packages", package.DirectPackages, allowed.Packages),
            ("frameworks", package.Frameworks, allowed.Frameworks ?? []),
        })
        {
            if (!actual.SetEquals(expected))
            {
                yield return $"{package.Name} references the {kind} [{Listed(actual)}]; section C allows [{Listed(expected)}].";
            }
        }

        var closure = package.RuntimePackages.Select(arrived => arrived.Id).Concat(package.Projects).Distinct();
        foreach (var arrived in closure.Order(StringComparer.Ordinal))
        {
            var keptOut = KeptOut.GetValueOrDefault(package.Name, []).Append("WolverineFx.RuntimeCompilation");
            if (keptOut.Any(prefix => arrived.StartsWith(prefix, StringComparison.Ordinal)))
            {
                yield return $"{package.Name}'s closure holds {arrived}, which section C keeps out of it.";
            }
        }

        if (NoPackages.Contains(package.Name) && package.RuntimePackages.Any())
        {
            yield return $"{package.Name}'s closure holds the packages [{Listed(package.RuntimePackages.Select(arrived => arrived.Id))}]; it must hold none.";
        }
    }

    private static string Listed(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.Ordinal));
}
