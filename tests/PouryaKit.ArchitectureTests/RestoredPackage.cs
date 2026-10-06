using System.Text.Json;
using System.Xml.Linq;

namespace PouryaKit.ArchitectureTests;

/// <summary>A package of the Kit's closure, with the licence expression its nuspec declares, if any.</summary>
internal sealed record PackageLicence(string Id, string Version, string? Expression);

/// <summary>
/// What restore resolved for one project, a Kit package or a project of the application, read from its
/// <c>obj/project.assets.json</c>: the references its project declares, and the full closure, every package and project
/// that arrives with it, directly or not, whether or not its compiled code uses them.
/// </summary>
internal sealed record RestoredPackage(
    string Name,
    IReadOnlySet<string> DirectProjects,
    IReadOnlySet<string> DirectPackages,
    IReadOnlySet<string> Frameworks,
    IReadOnlySet<string> Projects,
    IReadOnlyList<PackageLicence> Packages
)
{
    /// <summary>Every Kit package, one per folder under <c>src/</c>, as restored.</summary>
    public static IReadOnlyList<RestoredPackage> All { get; } =
    [
        .. Directory
            .GetDirectories(Path.Combine(Repository.Root, "src"))
            .Order(StringComparer.Ordinal)
            .Select(folder => Read(Path.GetFileName(folder), Path.Combine(folder, "obj", "project.assets.json"))),
    ];

    /// <summary>The project <paramref name="projectFile"/> (a <c>.csproj</c> path) as restored.</summary>
    public static RestoredPackage Of(string projectFile) =>
        Read(
            Path.GetFileNameWithoutExtension(projectFile),
            Path.Combine(Path.GetDirectoryName(projectFile)!, "obj", "project.assets.json")
        );

    private static RestoredPackage Read(string name, string assetsFile)
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var root = assets.RootElement;
        var project = root.GetProperty("project");
        var framework = project.GetProperty("frameworks").EnumerateObject().Single().Value;
        var restore = project.GetProperty("restore").GetProperty("frameworks").EnumerateObject().Single().Value;
        var packageFolder = root.GetProperty("packageFolders").EnumerateObject().First().Name;
        var libraries = root.GetProperty("libraries").EnumerateObject().ToList();

        return new RestoredPackage(
            name,
            Names(restore, "projectReferences").Select(path => Path.GetFileNameWithoutExtension(path)).ToHashSet(),
            Names(framework, "dependencies").ToHashSet(),
            // Every project targets the shared framework; only another framework reference is a choice.
            Names(framework, "frameworkReferences").Where(reference => reference != "Microsoft.NETCore.App").ToHashSet(),
            libraries
                .Where(library => library.Value.GetProperty("type").GetString() == "project")
                .Select(library => library.Name.Split('/')[0])
                .ToHashSet(),
            [
                .. libraries
                    .Where(library => library.Value.GetProperty("type").GetString() == "package")
                    .Select(library => Licence(library.Name, packageFolder, library.Value)),
            ]
        );
    }

    private static IEnumerable<string> Names(JsonElement element, string property) =>
        element.TryGetProperty(property, out var names) ? names.EnumerateObject().Select(name => name.Name) : [];

    private static PackageLicence Licence(string library, string packageFolder, JsonElement entry)
    {
        var (id, version) = (library.Split('/')[0], library.Split('/')[1]);
        var nuspec = entry.GetProperty("files").EnumerateArray().Select(file => file.GetString()!).Single(file => file.EndsWith(".nuspec", StringComparison.Ordinal));
        var licence = XDocument
            .Load(Path.Combine(packageFolder, entry.GetProperty("path").GetString()!, nuspec))
            .Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "license");
        return new PackageLicence(id, version, licence?.Attribute("type")?.Value == "expression" ? licence.Value.Trim() : null);
    }
}
