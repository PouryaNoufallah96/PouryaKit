using System.Text.RegularExpressions;

namespace PouryaKit.ArchitectureTests;

// ADR 0010: application code's namespaces follow its folders. IDE0130 holds the Proving Sample to it in the build, but it
// never reports a namespace that holds a partial type with a second declaration, such as a class whose logging methods a
// source generator completes, so this rule reads the namespace each source file declares.
public sealed class SampleNamespaceTests
{
    private static readonly string Source = Path.Combine(Repository.Root, "samples", "ModularMonolith", "src");

    // ADR 0010's one exception in the sample, by its path from Source. Aspire's convention: the service defaults extend
    // the host builder in Microsoft.Extensions.Hosting.
    private static readonly Dictionary<string, string> Exceptions = new()
    {
        ["ModularMonolith.ServiceDefaults/Extensions.cs"] = "Microsoft.Extensions.Hosting",
    };

    [Fact]
    public void Every_file_of_the_proving_sample_declares_its_projects_name_plus_its_folders()
    {
        var projects = Directory.GetFiles(Source, "*.csproj", SearchOption.AllDirectories);

        // Two Hosts, the service defaults, three Modules and Catalog's Contracts: a walk that finds none judges nothing, and
        // a walk that skips the files judges nothing either, so the partial type IDE0130 misses must be among the judged.
        Assert.Equal(7, projects.Length);
        var judged = projects.SelectMany(Judged).ToList();
        Assert.Contains(judged, file => file.Path == "ModularMonolith.Host/LoggedNewProductAnnouncer.cs");
        Assert.Empty(judged.SelectMany(file => Drift(file.Path, file.Expected, File.ReadAllText(file.FullPath))));
    }

    [Fact]
    public void A_namespace_other_than_its_projects_name_plus_its_folders_is_rejected()
    {
        const string Product = "Modules/Catalog/Catalog/Products/Entities/Product.cs";
        var expected = Expected("Catalog", "Products/Entities/Product.cs");

        Assert.Equal(
            [
                $"{Product} declares the namespace Catalog.Products; its project and folders make it Catalog.Products.Entities.",
                $"{Product} declares the namespace Catalog.Products.Entities.Stray; its project and folders make it Catalog.Products.Entities.",
                $"{Product} declares the namespace Catalog; its project and folders make it Catalog.Products.Entities.",
            ],
            [
                .. Drift(Product, expected, "using Catalog;\n\nnamespace Catalog.Products;\n\npublic sealed partial class Product;\n"),
                .. Drift(Product, expected, "namespace Catalog.Products.Entities.Stray;\n"),
                .. Drift(Product, expected, "namespace Catalog\n{\n    internal sealed class Product;\n}\n"),
                .. Drift(Product, expected, "namespace Catalog.Products.Entities;\n"),
            ]
        );
    }

    // The files a project is judged by, each with the namespace its folders make it: bin and obj hold build output;
    // Migrations hold dotnet-ef's output.
    private static IEnumerable<(string Path, string Expected, string FullPath)> Judged(string project)
    {
        var folder = Path.GetDirectoryName(project)!;
        return
            from file in Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories)
            let inProject = Path.GetRelativePath(folder, file).Replace('\\', '/')
            where !inProject.Split('/').Any(segment => segment is "bin" or "obj" or "Migrations")
            let path = Path.GetRelativePath(Source, file).Replace('\\', '/')
            select (path, Exceptions.GetValueOrDefault(path) ?? Expected(Path.GetFileNameWithoutExtension(project), inProject), file);
    }

    // No project of the sample sets a RootNamespace, so a project's root namespace is its name (ADR 0010).
    private static string Expected(string project, string inProject) => string.Join('.', [project, .. inProject.Split('/')[..^1]]);

    // Each namespace the file declares, file-scoped or as a block. A file of top-level statements, a Host's Program.cs,
    // declares none.
    private static IEnumerable<string> Drift(string path, string expected, string text) =>
        from Match declaration in Regex.Matches(text, @"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline)
        let declared = declaration.Groups[1].Value
        where declared != expected
        select $"{path} declares the namespace {declared}; its project and folders make it {expected}.";
}
