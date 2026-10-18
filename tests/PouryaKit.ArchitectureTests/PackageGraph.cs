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
        ["PouryaKit.Caching"] = new(["PouryaKit.Cqrs"], ["Microsoft.Extensions.Caching.Hybrid"]),
        ["PouryaKit.Core"] = new([], []),
        ["PouryaKit.Domain"] = new([], []),
        ["PouryaKit.Email"] = new(["PouryaKit.Domain"], []),
        // System.Security.Cryptography.Pkcs is a security floor over MimeKit's 10.0.0 (#124); Cqrs is for its composition check (#147).
        ["PouryaKit.Email.MailKit"] = new(
            ["PouryaKit.Cqrs", "PouryaKit.Email"],
            ["MailKit", "Microsoft.Extensions.Options", "System.Security.Cryptography.Pkcs"]
        ),
        // Cqrs builds the Host's validated container and runs its composition checks as it starts (#61, 2026-10-07),
        // which takes the container's implementation and the Hosting abstractions from Modularity.
        ["PouryaKit.Cqrs"] = new(
            ["PouryaKit.Core", "PouryaKit.Domain"],
            [
                "Microsoft.Extensions.DependencyInjection",
                "Microsoft.Extensions.DependencyInjection.Abstractions",
                "Microsoft.Extensions.Hosting.Abstractions",
                "Microsoft.Extensions.Logging.Abstractions",
            ]
        ),
        ["PouryaKit.Validation"] = new(["PouryaKit.Cqrs"], ["FluentValidation"]),
        ["PouryaKit.Modularity"] = new(["PouryaKit.Cqrs"], ["Microsoft.Extensions.Hosting.Abstractions"]),
        ["PouryaKit.Persistence"] = new(["PouryaKit.Domain"], ["Ardalis.Specification"]),
        ["PouryaKit.Persistence.EFCore"] = new(
            ["PouryaKit.Core", "PouryaKit.Cqrs", "PouryaKit.Persistence"],
            ["Microsoft.EntityFrameworkCore.Relational", "Ardalis.Specification.EntityFrameworkCore", "Microsoft.Extensions.Hosting.Abstractions"]
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
        ["PouryaKit.Messaging.Wolverine.RabbitMq"] = new(["PouryaKit.Messaging.Wolverine"], ["WolverineFx.RabbitMQ"]),
        ["PouryaKit.Observability"] = new(["PouryaKit.Cqrs"], []),
        ["PouryaKit.Security"] = new(["PouryaKit.Core", "PouryaKit.Cqrs"], []),
        ["PouryaKit.Security.JwtBearer"] = new(["PouryaKit.Security"], ["Microsoft.AspNetCore.Authentication.JwtBearer"]),
        ["PouryaKit.Web"] = new(
            ["PouryaKit.Core", "PouryaKit.Cqrs"],
            ["Microsoft.Extensions.Http.Resilience"],
            ["Microsoft.AspNetCore.App"]
        ),
        ["PouryaKit.Web.OpenApi"] = new(
            ["PouryaKit.Web"],
            ["Asp.Versioning.Http", "Asp.Versioning.OpenApi", "Microsoft.AspNetCore.OpenApi", "Scalar.AspNetCore"],
            ["Microsoft.AspNetCore.App"]
        ),
        // dotnet new templates, content only: the projects they generate reference the Kit's packages, the package none.
        ["PouryaKit.Templates"] = new([], []),
        ["PouryaKit.Testing"] = new(
            ["PouryaKit.Modularity", "PouryaKit.Persistence.Postgres", "PouryaKit.Messaging.Wolverine.EFCore"],
            ["Testcontainers.PostgreSql", "Respawn", "xunit.v3.extensibility.core", "Microsoft.Extensions.TimeProvider.Testing", "WolverineFx.RuntimeCompilation"]
        ),
        // Storage references Cqrs for the one-provider composition check both providers register through it (#128).
        ["PouryaKit.Storage"] = new(["PouryaKit.Cqrs", "PouryaKit.Domain"], []),
        ["PouryaKit.Storage.S3"] = new(["PouryaKit.Storage"], ["AWSSDK.S3", "Microsoft.Extensions.Options"]),
        // Apache.Arrow and System.Security.Cryptography.ProtectedData are licence floors over the versions Azure.Storage.Blobs
        // 12.30.1 brings, which declare no licence expression (#128).
        ["PouryaKit.Storage.AzureBlob"] = new(
            ["PouryaKit.Storage"],
            ["Azure.Storage.Blobs", "Microsoft.Extensions.Options", "Apache.Arrow", "System.Security.Cryptography.ProtectedData"]
        ),
        ["PouryaKit.Localization"] = new(["PouryaKit.Cqrs"], [], ["Microsoft.AspNetCore.App"]),
        ["PouryaKit.ReverseProxy"] = new([], ["Microsoft.Extensions.ServiceDiscovery.Yarp", "Yarp.ReverseProxy"], ["Microsoft.AspNetCore.App"]),
        // The server and client libraries, not the Grpc.AspNetCore metapackage, which carries Grpc.Tools to every consumer (#134).
        ["PouryaKit.Grpc"] = new(["PouryaKit.Web"], ["Grpc.AspNetCore.Server", "Grpc.Net.ClientFactory"], ["Microsoft.AspNetCore.App"]),
        // AppHost-only: the containers of a Host's local infrastructure. MessagePack is a security floor over Aspire.Hosting's
        // 2.5.302, Fractions a licence-expression floor over KubernetesClient's 7.3.0 (#130).
        ["PouryaKit.Aspire"] = new(
            [],
            ["Aspire.Hosting.PostgreSQL", "Aspire.Hosting.RabbitMQ", "Aspire.Hosting.Redis", "Fractions", "MessagePack"]
        ),
        ["PouryaKit.Realtime"] = new(["PouryaKit.Persistence.EFCore", "PouryaKit.Web"], ["Npgsql"], ["Microsoft.AspNetCore.App"]),
        // System.Security.Cryptography.Pkcs is a security floor over the 8.0.1 PDFsharp 6.2.4 asks for (#129).
        ["PouryaKit.Reporting"] = new(
            ["PouryaKit.Domain"],
            ["CsvHelper", "DocumentFormat.OpenXml", "PDFsharp-MigraDoc", "System.Security.Cryptography.Pkcs"]
        ),
        // The OpenAI client serves all three providers (A1); Modularity is the Gateway tool's (#135), the Agent Framework the agents' (#136).
        ["PouryaKit.AI"] = new(
            ["PouryaKit.Cqrs", "PouryaKit.Modularity"],
            ["Microsoft.Agents.AI", "Microsoft.Agents.AI.Workflows", "Microsoft.Extensions.AI", "Microsoft.Extensions.AI.OpenAI", "Microsoft.Extensions.Http", "Microsoft.Extensions.Options"]
        ),
        // Semantic search on the Unit of Work's connection, sending embeddings as real[], which the server's pgvector casts (#138).
        ["PouryaKit.AI.Postgres"] = new(["PouryaKit.AI", "PouryaKit.Persistence.Postgres"], []),
        // The Stream write model's Unit of Work (#131): Npgsql is the Kit's line over the 9.0.4 Marten 9.46.0 brings, for the
        // Kit's statements it runs on its own transaction; Messaging is not needed, the Outbox step being a seam.
        ["PouryaKit.Persistence.Marten"] = new(["PouryaKit.Cqrs", "PouryaKit.Persistence"], ["Marten", "Npgsql"]),
        ["PouryaKit.Messaging.Wolverine.Marten"] = new(["PouryaKit.Messaging.Wolverine", "PouryaKit.Persistence.Marten"], ["WolverineFx.Marten"]),
    };

    // What must never arrive in a package's closure, by package id prefix or project name. Every package but Testing also
    // keeps WolverineFx.RuntimeCompilation out: Roslyn at run time is the Host's choice in Development, and a test Host's
    // (ADR 0004).
    private static readonly Dictionary<string, string[]> KeptOut = new()
    {
        ["PouryaKit.Persistence.Dapper"] = ["Microsoft.EntityFrameworkCore"],
        ["PouryaKit.Persistence.Postgres"] = ["WolverineFx", "JasperFx", "PouryaKit.Messaging"],
        // Outgoing HTTP resilience is the standard handler alone, usable without an AppHost or ServiceDefaults.
        // API versioning and OpenAPI are the optional Web.OpenApi's, so a Host without them takes none of it.
        ["PouryaKit.Web"] = ["PouryaKit.Modularity", "Aspire", "Asp.Versioning", "Microsoft.AspNetCore.OpenApi", "Microsoft.OpenApi", "Scalar"],
        // The token's claims reach Web as claims; the bearer scheme knows nothing of the HTTP endpoints that read them.
        ["PouryaKit.Security.JwtBearer"] = ["PouryaKit.Web"],
        // The native OpenAPI documents and Scalar are the one OpenAPI stack: no Swagger generator beside them.
        ["PouryaKit.Web.OpenApi"] = ["PouryaKit.Modularity", "Aspire", "Swashbuckle", "NSwag"],
        // Transport- and database-neutral: no EF Core, no PostgreSQL driver, no message store.
        ["PouryaKit.Messaging.Wolverine"] = ["Microsoft.EntityFrameworkCore", "Npgsql", "WolverineFx.RDBMS", "WolverineFx.Postgresql", "Weasel"],
        // A transport between Hosts, neutral to the database as well: its envelopes go through the Host's own store.
        ["PouryaKit.Messaging.Wolverine.RabbitMq"] = ["Microsoft.EntityFrameworkCore", "Npgsql", "WolverineFx.RDBMS", "WolverineFx.Postgresql", "Weasel"],
        // It emits through System.Diagnostics alone: exporters and logging are the Host's (I.3 Observability, #47).
        ["PouryaKit.Observability"] = ["OpenTelemetry", "Serilog", "WolverineFx", "PouryaKit.Messaging"],
        // Redis is the application's own distributed cache for HybridCache, never the Kit's (I.3 Caching, #43).
        ["PouryaKit.Caching"] = ["StackExchange.Redis", "Microsoft.Extensions.Caching.StackExchangeRedis"],
        // The Reverse proxy runs no Operation: no Kit package, the dispatcher's included, arrives in its closure (#126).
        ["PouryaKit.ReverseProxy"] = ["PouryaKit."],
        // Compiling a .proto is the project's that holds it, with private assets (#134).
        ["PouryaKit.Grpc"] = ["Grpc.Tools"],
        // A storage provider's SDK stays in its own package: the contract takes none.
        ["PouryaKit.Storage"] = ["AWSSDK", "Azure"],
        // PostgreSQL carries a push between instances: no message store, broker or Redis, and no Messaging (#133).
        ["PouryaKit.Realtime"] = ["WolverineFx", "JasperFx", "PouryaKit.Messaging", "RabbitMQ", "StackExchange.Redis"],
        // The libraries of spec #115's E1 are the one renderer: QuestPDF and ClosedXML fail the licence test, and no second
        // file library arrives beside them, a storage SDK included: the renderer returns bytes and stores nothing (#129).
        ["PouryaKit.Reporting"] = ["QuestPDF", "ClosedXML", "PouryaKit.Storage", "AWSSDK", "Azure"],
        // One client for three providers, OllamaSharp and the Azure client dropped (A1), and no resilience of its own:
        // the provider's HTTP client keeps the Host's one pipeline (#135).
        ["PouryaKit.AI"] = ["OllamaSharp", "Azure.AI.OpenAI", "Microsoft.Extensions.Http.Resilience", "Microsoft.Extensions.Resilience", "Polly"],
        // A Stream's commit owner takes no message store and no EF Core: Wolverine's Marten outbox is its messaging package's (#131).
        ["PouryaKit.Persistence.Marten"] = ["WolverineFx", "Microsoft.EntityFrameworkCore"],
        // A Host of Stream Modules alone takes no EF Core.
        ["PouryaKit.Messaging.Wolverine.Marten"] = ["Microsoft.EntityFrameworkCore", "PouryaKit.Persistence.EFCore"],
    };

    // Core and Domain, and Messaging and the Email contract (Domain only), bring no package at all; nor do the templates.
    private static readonly HashSet<string> NoPackages = ["PouryaKit.Core", "PouryaKit.Domain", "PouryaKit.Email", "PouryaKit.Messaging", "PouryaKit.Templates"];

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
            // Every package but the AppHost-only one keeps Aspire's packages out: they run in an AppHost, never in a Host (#130).
            var keptOut = KeptOut
                .GetValueOrDefault(package.Name, [])
                .Concat(package.Name == "PouryaKit.Testing" ? [] : ["WolverineFx.RuntimeCompilation"])
                .Concat(package.Name == "PouryaKit.Aspire" ? [] : ["Aspire"])
                // and every package but AI.Postgres keeps pgvector out, its client and the package that creates it (#138).
                .Concat(package.Name == "PouryaKit.AI.Postgres" ? [] : ["Pgvector", "PouryaKit.AI.Postgres"]);
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
