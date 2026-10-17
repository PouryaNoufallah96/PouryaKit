using System.Xml.Linq;
using static PouryaKit.ArchitectureTests.TemplateRules;

namespace PouryaKit.ArchitectureTests;

// Ticket #53: the monolith and modular-monolith templates of PouryaKit.Templates. Each rule judges the package's content,
// then an offending variant of it, so a rule that reads nothing fails. Generating, building and running both templates
// from the packed package is scripts/packaging/validate.py's.
public sealed class TemplateTests
{
    // The excluded sample file the monolith needs nothing in place of: ICatalogProducts answers other Modules, and the
    // monolith has none.
    private static readonly string[] Unreplaced = ["src/Modules/Catalog/Catalog/Products/Features/GetProduct/CatalogProducts.cs"];

    [Fact]
    public void The_template_content_holds_no_copy_of_a_proving_sample_file()
    {
        var content = Files(Content);

        Assert.Empty(CopiesOfTheSample(content, PackedSample));
        var copied = PackedSample.Single(file => file.Path == "Modules/Catalog/Catalog/Products/Errors/ProductErrors.cs");
        Assert.Equal(
            ["monolith/src/Modules/Catalog/Catalog/Products/Errors/ProductErrors.cs is a copy of the Proving Sample's Modules/Catalog/Catalog/Products/Errors/ProductErrors.cs; pack it from the sample instead."],
            CopiesOfTheSample([.. content, copied with { Path = "monolith/src/" + copied.Path }], PackedSample)
        );
    }

    [Fact]
    public void Nothing_the_monolith_generates_names_modularity_and_the_modular_monolith_does()
    {
        var monolith = Generated(Monolith);

        Assert.Empty(ModularityIn(monolith));
        Assert.NotEmpty(ModularityIn(Generated(Modular)));
        // The rule reads the smoke test, which runs on PouryaKit.Testing and names none of the Modularity it depends on.
        Assert.Contains(monolith, file => file.Path.EndsWith(".Tests.csproj", StringComparison.Ordinal) && file.Text.Contains("\"PouryaKit.Testing\"", StringComparison.Ordinal));
        var endpoint = PackedSample.Single(file => file.Path.EndsWith("/CreateCategoryEndpoint.cs", StringComparison.Ordinal));
        Assert.Equal(
            [
                "src/Modules/Catalog/Catalog/Catalog.csproj names PouryaKit.Modularity, but the monolith composes no Modularity.",
                "src/Modules/Catalog/Catalog/Categories/Features/CreateCategory/CreateCategoryEndpoint.cs names IGatewayProcessor, but the monolith composes no Modularity.",
                "src/Modules/Catalog/Catalog/Categories/Features/CreateCategory/CreateCategoryEndpoint.cs names PouryaKit.Modularity, but the monolith composes no Modularity.",
                "src/Program.cs names AddPouryaKitModules, but the monolith composes no Modularity.",
            ],
            ModularityIn(
                [
                    .. monolith.Where(file => !file.Path.EndsWith("/CreateCategoryEndpoint.cs", StringComparison.Ordinal)),
                    endpoint with { Path = "src/" + endpoint.Path },
                    new("src/Modules/Catalog/Catalog/Catalog.csproj", """<Project><ItemGroup><PackageReference Include="PouryaKit.Modularity" /></ItemGroup></Project>"""),
                    new("src/Program.cs", "builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>());"),
                ]
            ).Order(StringComparer.Ordinal)
        );
    }

    [Fact]
    public void Every_sample_file_the_monolith_excludes_has_the_monoliths_own_file_in_its_place()
    {
        var excluded = ExcludedFromSample(Monolith);
        var replacements = Own(Monolith).Select(file => file.Path).Where(path => path.StartsWith("src/Modules/", StringComparison.Ordinal) && !path.EndsWith(".csproj", StringComparison.Ordinal)).ToList();

        Assert.Empty(UnreplacedExclusions(excluded, replacements, Unreplaced));
        // The rule reads what the globs exclude: the Composition Root and the Gateway endpoints, none of them a template file.
        Assert.Contains("src/Modules/Catalog/Catalog/CatalogModule.cs", excluded);
        Assert.Contains("src/Modules/Catalog/Catalog/Products/Features/CreateProduct/CreateProductEndpoint.cs", excluded);
        Assert.Equal(
            [
                "The monolith excludes the sample's src/Modules/Catalog/Catalog/Products/Features/RemoveProducts/RemoveProductsEndpoint.cs but has no file of its own there.",
                "The monolith's src/Modules/Catalog/Catalog/Products/Features/Stray.cs replaces no file it excludes from the sample.",
            ],
            UnreplacedExclusions(
                [.. excluded, "src/Modules/Catalog/Catalog/Products/Features/RemoveProducts/RemoveProductsEndpoint.cs"],
                [.. replacements, "src/Modules/Catalog/Catalog/Products/Features/Stray.cs"],
                Unreplaced
            )
        );
    }

    // Ticket #87: the monolith's replacements are written by hand after the sample's files, so a change to Catalog's routes,
    // versions, anonymous Actor, Idempotency-Key, endpoint names or registrations in the sample fails here, not in a
    // generated Host that silently serves something else.
    [Fact]
    public void Each_file_the_monolith_replaces_makes_the_sample_files_calls_but_for_the_composition()
    {
        var replacements = Own(Monolith)
            .Where(file => file.Path.StartsWith("src/Modules/", StringComparison.Ordinal) && file.Path.EndsWith(".cs", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(MirrorDifferences(replacements, PackedSample));
        Assert.Equal(7, replacements.Count);
        var module = replacements.Single(file => file.Path.EndsWith("/CatalogModule.cs", StringComparison.Ordinal));
        var create = replacements.Single(file => file.Path.EndsWith("/CreateProductEndpoint.cs", StringComparison.Ordinal));
        var rename = replacements.Single(file => file.Path.EndsWith("/RenameProductEndpoint.cs", StringComparison.Ordinal));
        const string V1 = """services.Publish<ProductCreatedV1>("catalog.product-created.v1");""";
        const string V2 = """services.Publish<ProductCreatedV2>("catalog.product-created.v2");""";
        // The same calls in another order differ.
        Assert.Equal(
            [
                """src/Modules/Catalog/Catalog/CatalogModule.cs calls Publish<ProductCreatedV2>("catalog.product-created.v2") where the sample's Modules/Catalog/Catalog/CatalogModule.cs calls Publish<ProductCreatedV1>("catalog.product-created.v1").""",
            ],
            MirrorDifferences(
                [
                    .. replacements.Except([module]),
                    module with { Text = module.Text.Replace(V1, "services.Swapped();", StringComparison.Ordinal).Replace(V2, V1, StringComparison.Ordinal).Replace("services.Swapped();", V2, StringComparison.Ordinal) },
                ],
                PackedSample
            )
        );
        Assert.Equal(
            [
                """src/Modules/Catalog/Catalog/CatalogModule.cs calls Publish<ProductCreatedV2>("catalog.product-created.v3") where the sample's Modules/Catalog/Catalog/CatalogModule.cs calls Publish<ProductCreatedV2>("catalog.product-created.v2").""",
                "src/Modules/Catalog/Catalog/Products/Features/CreateProduct/CreateProductEndpoint.cs calls nothing more where the sample's Modules/Catalog/Catalog/Products/Features/CreateProduct/CreateProductEndpoint.cs calls RequireIdempotencyKey().",
                """src/Modules/Catalog/Catalog/Products/Features/RenameProduct/RenameProductEndpoint.cs calls MapPut("/products/{productId}/title", async (…) => (…).ToHttpResult(…)) where the sample's Modules/Catalog/Catalog/Products/Features/RenameProduct/RenameProductEndpoint.cs calls MapPut("/products/{productId}/name", async (…) => (…).ToHttpResult(…)).""",
            ],
            MirrorDifferences(
                [
                    .. replacements.Except([module, create, rename]),
                    module with { Text = module.Text.Replace("catalog.product-created.v2", "catalog.product-created.v3", StringComparison.Ordinal) },
                    create with { Text = create.Text.Replace(".RequireIdempotencyKey()", "", StringComparison.Ordinal) },
                    rename with { Text = rename.Text.Replace("/products/{productId}/name", "/products/{productId}/title", StringComparison.Ordinal) },
                ],
                PackedSample
            ).Order(StringComparer.Ordinal)
        );
    }

    // Ticket #87: each template Host's pipeline is the sample Host's, which the Kit's own tests run.
    [Fact]
    public void Each_template_host_builds_the_sample_hosts_pipeline_in_its_order()
    {
        var sample = new TextFile("ModularMonolith.Host/ModularMonolithHost.cs", File.ReadAllText(Path.Combine(SampleRoot, "ModularMonolith.Host", "ModularMonolithHost.cs")));

        Assert.All(
            [Monolith, Modular],
            template =>
            {
                var host = Own(template).Single(file => file.Text.Contains("app.UseRouting();", StringComparison.Ordinal));
                Assert.Empty(PipelineDifferences(host, sample));
                Assert.Contains("app.MapDefaultEndpoints();", host.Text, StringComparison.Ordinal);
            }
        );
        var monolith = Own(Monolith).Single(file => file.Text.Contains("app.UseRouting();", StringComparison.Ordinal));
        Assert.Equal(
            [
                $"{monolith.Path} builds its pipeline with UseExceptionHandler, UseRouting, UseCors, UseAuthorization, UseAuthentication, UseRateLimiter, MapPouryaKitModuleEndpoints, MapPouryaKitGrpcServices, MapPouryaKitOpenApi; the sample's ModularMonolith.Host/ModularMonolithHost.cs with UseExceptionHandler, UseRouting, UseCors, UseAuthentication, UseAuthorization, UseRateLimiter, MapPouryaKitModuleEndpoints, MapPouryaKitGrpcServices, MapPouryaKitOpenApi.",
            ],
            PipelineDifferences(
                monolith with
                {
                    Text = monolith
                        .Text.Replace("app.UseAuthentication();", "app.Swapped();", StringComparison.Ordinal)
                        .Replace("app.UseAuthorization();", "app.UseAuthentication();", StringComparison.Ordinal)
                        .Replace("app.Swapped();", "app.UseAuthorization();", StringComparison.Ordinal),
                },
                sample
            )
        );
    }

    [Fact]
    public void Each_template_project_references_what_the_samples_project_at_its_place_does()
    {
        (string Template, string Path, string Sample, string[] Dropped)[] projects =
        [
            (Modular, "src/Modules/Billing/Billing/Billing.csproj", "Modules/Billing/Billing/Billing.csproj", []),
            (Modular, "src/Modules/Catalog/Catalog/Catalog.csproj", "Modules/Catalog/Catalog/Catalog.csproj", []),
            (Modular, "src/Modules/Inventory/Inventory/Inventory.csproj", "Modules/Inventory/Inventory/Inventory.csproj", []),
            (Monolith, "src/Modules/Catalog/Catalog/Catalog.csproj", "Modules/Catalog/Catalog/Catalog.csproj", ["PouryaKit.Modularity"]),
            (Shared, "src/Modules/Catalog/Catalog.Contracts/Catalog.Contracts.csproj", "Modules/Catalog/Catalog.Contracts/Catalog.Contracts.csproj", []),
            (Shared, "src/ModularMonolith.ServiceDefaults/ModularMonolith.ServiceDefaults.csproj", "ModularMonolith.ServiceDefaults/ModularMonolith.ServiceDefaults.csproj", []),
        ];

        Assert.All(
            projects,
            project =>
                Assert.Empty(
                    ReferenceDifferences(
                        project.Path,
                        File.ReadAllText(Path.Combine(Content, project.Template, project.Path)),
                        File.ReadAllText(Path.Combine(SampleRoot, project.Sample)),
                        project.Dropped
                    )
                )
        );
        var catalog = XDocument.Load(Path.Combine(Content, Modular, "src/Modules/Catalog/Catalog/Catalog.csproj"));
        catalog.Descendants("PackageReference").Single(reference => reference.Attribute("Include")!.Value == "PouryaKit.Caching").Remove();
        catalog.Descendants("ItemGroup").First().Add(new XElement("PackageReference", new XAttribute("Include", "Dapper")));
        Assert.Equal(
            [
                "Catalog.csproj does not reference PouryaKit.Caching, which the sample's project does.",
                "Catalog.csproj references Dapper, which the sample's project does not.",
            ],
            ReferenceDifferences("Catalog.csproj", catalog.ToString(), File.ReadAllText(Path.Combine(SampleRoot, "Modules/Catalog/Catalog/Catalog.csproj")))
        );
    }

    [Fact]
    public void Each_generated_host_joins_a_broker_under_the_service_name_its_template_derives_the_same_way_in_both()
    {
        var (token, derivation) = DerivedSymbol(Monolith, "serviceName");

        Assert.Equal(derivation, DerivedSymbol(Modular, "serviceName").Derivation);
        Assert.All(
            [Monolith, Modular],
            template =>
            {
                Assert.Empty(UnderivedServiceNames(Generated(template), token));
                Assert.Contains(Generated(template), file => file.Text.Contains($"serviceName: \"{token}\"", StringComparison.Ordinal));
            }
        );
        Assert.Equal(
            ["src/ModularMonolith.Host/Program.cs passes the service name ModularMonolith; pass modular-monolith, which the template's serviceName symbol derives into a valid queue name from any project name."],
            UnderivedServiceNames(
                [new("src/ModularMonolith.Host/Program.cs", """builder.UsePouryaKitRabbitMq(connectionName: "broker", serviceName: "ModularMonolith");""")],
                token
            )
        );
    }

    // Ticket #64: the service defaults exempt /alive from the rate limit through its endpoint, which the rate limiting
    // middleware sees only after routing, and the Kit cannot detect a limiter placed before it.
    [Fact]
    public void Each_generated_host_routes_before_it_rate_limits()
    {
        Assert.All(
            [Monolith, Modular],
            template =>
            {
                Assert.Empty(RateLimitersBeforeRouting(Generated(template)));
                Assert.Contains(Generated(template), file => file.Text.Contains("app.UseRateLimiter();", StringComparison.Ordinal));
            }
        );
        Assert.Equal(
            ["src/ModularMonolith.Host/Program.cs calls UseRouting() after UseRateLimiter(); route first, or the rate limit ignores the exemption of /alive and the Actor an anonymous endpoint names."],
            RateLimitersBeforeRouting([new("src/ModularMonolith.Host/Program.cs", "app.UseRateLimiter();\napp.UseRouting();")])
        );
    }

    // Ticket #68: an application without Modules has a Unit of Work in its request scope, so an endpoint, a service or
    // the Host could take a repository and change an aggregate outside a command, which the Kit cannot refuse.
    [Fact]
    public void None_of_the_monoliths_own_files_takes_a_repository()
    {
        IReadOnlyList<TextFile> own = [.. Own(Monolith), .. Own(Shared)];

        Assert.Empty(RepositoriesTaken(own, PackedSample));
        // The rule reads the Composition Root, which registers the sample's repository and takes none.
        Assert.Contains(own, file => file.Text.Contains("AddScoped<IProductRepository, ProductRepository>()", StringComparison.Ordinal));
        var endpoint = own.Single(file => file.Path.EndsWith("/RenameProductEndpoint.cs", StringComparison.Ordinal));
        Assert.Equal(
            [
                "src/ModularMonolith.Host/Program.cs takes IRepository<Product, ProductId>; send a command through IDispatcher instead: a handler takes the repository, and a change made outside a command is dropped or saved by a command that never made it.",
                "src/Modules/Catalog/Catalog/Products/Features/RenameProduct/ProductRenamer.cs takes IProductRepository; send a command through IDispatcher instead: a handler takes the repository, and a change made outside a command is dropped or saved by a command that never made it.",
                "src/Modules/Catalog/Catalog/Products/Features/RenameProduct/RenameProductEndpoint.cs takes IRepository<Product, ProductId>; send a command through IDispatcher instead: a handler takes the repository, and a change made outside a command is dropped or saved by a command that never made it.",
            ],
            RepositoriesTaken(
                [
                    .. own.Where(file => file != endpoint && file.Path != "src/ModularMonolith.Host/Program.cs"),
                    endpoint with { Text = endpoint.Text.Replace("IDispatcher dispatcher", "IRepository<Product, ProductId> products", StringComparison.Ordinal) },
                    new("src/Modules/Catalog/Catalog/Products/Features/RenameProduct/ProductRenamer.cs", "internal sealed class ProductRenamer(IProductRepository products);"),
                    new("src/ModularMonolith.Host/Program.cs", "// IProductRepository is Catalog's own.\nvar products = scope.ServiceProvider.GetRequiredService<IRepository<Product, ProductId>>();"),
                ],
                PackedSample
            ).Order(StringComparer.Ordinal)
        );
    }

    // Ticket #59: -n acme-shop names the folders acme-shop.Host, so the solution and the project references must too.
    [Fact]
    public void Each_template_names_its_projects_paths_through_the_name_as_typed_the_same_way_in_both()
    {
        var (token, derivation) = DerivedSymbol(Monolith, "projectName");
        var sourceName = SourceName(Monolith);

        Assert.Equal(derivation, DerivedSymbol(Modular, "projectName").Derivation);
        Assert.Equal(sourceName, SourceName(Modular));
        // The engine replaces every form of the sourceName in content, each a casing of it, so a token holding one breaks.
        Assert.DoesNotContain(sourceName, token, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            [Monolith, Modular],
            template =>
            {
                Assert.Empty(ProjectPathDefects(Generated(template), sourceName, token));
                Assert.Contains(Generated(template), file => file.Path.EndsWith(".slnx", StringComparison.Ordinal) && file.Text.Contains(token, StringComparison.Ordinal));
            }
        );
        var generated = Generated(Modular);
        var solution = generated.Single(file => file.Path == "ModularMonolith.slnx");
        var host = generated.Single(file => file.Path == "src/ModularMonolith.Host/ModularMonolith.Host.csproj");
        Assert.Equal(
            [
                "ModularMonolith.slnx spells the sourceName ModularMonolith, which the engine writes as the name a namespace can hold (acme_shop for -n acme-shop), not as it names the files; name a project's path through PouryaKitAppName.",
                @"src/ModularMonolith.Host/ModularMonolith.Host.csproj names ..\PouryaKitAppName.Defaults\PouryaKitAppName.Defaults.csproj, which is no project the template generates.",
            ],
            ProjectPathDefects(
                [
                    .. generated.Except([solution, host]),
                    solution with { Text = solution.Text.Replace($"src/{token}.Host/{token}.Host.csproj", "src/ModularMonolith.Host/ModularMonolith.Host.csproj", StringComparison.Ordinal) },
                    host with { Text = host.Text.Replace($@"..\{token}.ServiceDefaults\{token}.ServiceDefaults.csproj", @"..\PouryaKitAppName.Defaults\PouryaKitAppName.Defaults.csproj", StringComparison.Ordinal) },
                ],
                sourceName,
                token
            )
        );
    }

    [Fact]
    public void Each_template_pins_the_kits_third_party_versions_and_one_kit_version_that_packing_writes()
    {
        var kit = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Packages.props"));
        var token = XDocument
            .Load(Path.Combine(Repository.Root, "src", "PouryaKit.Templates", "PouryaKit.Templates.csproj"))
            .Descendants("_KitVersionToken")
            .Single()
            .Value;

        Assert.All(
            [Monolith, Modular],
            template => Assert.Empty(VersionDifferences(template, File.ReadAllText(Path.Combine(Content, template, "Directory.Packages.props")), kit, token))
        );
        var changed = XDocument.Load(Path.Combine(Content, Modular, "Directory.Packages.props"));
        changed.Descendants("PackageVersion").Single(version => version.Attribute("Include")!.Value == "xunit.v3").SetAttributeValue("Version", "4.0.0");
        changed.Descendants("PackageVersion").Single(version => version.Attribute("Include")!.Value == "PouryaKit.Web").SetAttributeValue("Version", "0.1.0");
        changed.Descendants("PouryaKitVersion").Single().Value = "0.1.0";
        Assert.Equal(
            [
                $"modular does not hold the placeholder {token} that packing replaces with the Kit's version.",
                "modular pins PouryaKit.Web at 0.1.0; every Kit package has $(PouryaKitVersion).",
                "modular pins xunit.v3 at 4.0.0; the Kit has 4.0.1.",
            ],
            VersionDifferences(Modular, changed.ToString(), kit, token)
        );
    }
}
