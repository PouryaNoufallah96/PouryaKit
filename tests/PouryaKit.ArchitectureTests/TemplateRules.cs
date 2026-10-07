using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using PouryaKit.Modularity;

namespace PouryaKit.ArchitectureTests;

/// <summary>A file by its path, relative and with forward slashes, and its text.</summary>
internal sealed record TextFile(string Path, string Text);

/// <summary>
/// The template package's rules (ticket #53), each a function from files to violations, so the same function judges
/// the package and an offending set. A template generates what its <c>template.json</c> sources select: its own folder,
/// the folder both templates share, and the Proving Sample's files the package packs.
/// </summary>
internal static class TemplateRules
{
    public const string Monolith = "monolith";
    public const string Modular = "modular";
    public const string Shared = "shared";

    /// <summary>The folder of the package's template content, as the package's <c>content/</c> holds it.</summary>
    public static readonly string Content = Path.Combine(Repository.Root, "src", "PouryaKit.Templates", "content");

    public static readonly string SampleRoot = Path.Combine(Repository.Root, "samples", "ModularMonolith", "src");

    // The engine's default exclusions of a source that names none.
    private static readonly string[] DefaultExclusions = ["**/[Bb]in/**", "**/[Oo]bj/**", ".template.config/**/*", "**/*.filelist", "**/*.user", "**/*.lock.json"];

    /// <summary>Every file under <paramref name="root"/>, build output left out.</summary>
    public static IReadOnlyList<TextFile> Files(string root) =>
        [
            .. Directory
                .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
                .Where(path => !Regex.IsMatch(path, "(^|/)(bin|obj)/"))
                .Order(StringComparer.Ordinal)
                .Select(path => new TextFile(path, File.ReadAllText(Path.Combine(root, path)))),
        ];

    /// <summary>
    /// The Proving Sample's files the package packs under <c>content/sample/</c>, as its project file packs them: every
    /// Module's C# files, the Host's permission source and announcer, and the service defaults.
    /// </summary>
    public static IReadOnlyList<TextFile> PackedSample { get; } =
        [
            .. Files(SampleRoot)
                .Where(file =>
                    (file.Path.StartsWith("Modules/", StringComparison.Ordinal) && file.Path.EndsWith(".cs", StringComparison.Ordinal))
                    || file.Path
                        is "ModularMonolith.Host/CallerPermissions.cs"
                            or "ModularMonolith.Host/LoggedNewProductAnnouncer.cs"
                            or "ModularMonolith.ServiceDefaults/Extensions.cs"
                ),
        ];

    /// <summary>What <paramref name="template"/> generates, by target path, before the engine renames anything.</summary>
    public static IReadOnlyList<TextFile> Generated(string template) =>
        [.. Sources(template).SelectMany(source => source.Selected.Select(file => file with { Path = source.Target + file.Path }))];

    /// <summary>The sample files <paramref name="template"/>'s sources select but exclude, by target path.</summary>
    public static IReadOnlyList<string> ExcludedFromSample(string template) =>
        [.. Sources(template).Where(source => source.FromSample).SelectMany(source => source.Excluded.Select(file => source.Target + file.Path))];

    /// <summary>The files of <paramref name="template"/>'s own folder, its configuration left out.</summary>
    public static IReadOnlyList<TextFile> Own(string template) =>
        [.. Files(Path.Combine(Content, template)).Where(file => !file.Path.StartsWith(".template.config/", StringComparison.Ordinal))];

    private sealed record Source(string Target, bool FromSample, IReadOnlyList<TextFile> Selected, IReadOnlyList<TextFile> Excluded);

    private static IEnumerable<Source> Sources(string template)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(Content, template, ".template.config", "template.json")));
        foreach (var source in configuration.RootElement.GetProperty("sources").EnumerateArray())
        {
            var folder = source.GetProperty("source").GetString()!;
            var fromSample = folder == "../sample/";
            var files = fromSample ? PackedSample : Files(Path.GetFullPath(Path.Combine(Content, template, folder)));
            var include = Patterns(source, "include", ["**/*"]);
            var exclude = Patterns(source, "exclude", DefaultExclusions);
            var included = files.Where(file => Matches(include, file.Path)).ToList();
            yield return new Source(
                source.GetProperty("target").GetString()!.TrimStart('.', '/'),
                fromSample,
                [.. included.Where(file => !Matches(exclude, file.Path))],
                [.. included.Where(file => Matches(exclude, file.Path))]
            );
        }
    }

    private static string[] Patterns(JsonElement source, string name, string[] fallback) =>
        source.TryGetProperty(name, out var patterns) ? [.. patterns.EnumerateArray().Select(pattern => pattern.GetString()!)] : fallback;

    private static bool Matches(string[] patterns, string path)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(patterns);
        return matcher.Match(path).HasMatches;
    }

    /// <summary>
    /// The template content holds no copy of a Proving Sample file: business code is packed from the sample, so it
    /// is never maintained twice.
    /// </summary>
    public static IEnumerable<string> CopiesOfTheSample(IEnumerable<TextFile> content, IReadOnlyList<TextFile> sample) =>
        from file in content
        from original in sample
        where Normalized(file.Text) == Normalized(original.Text)
        select $"{file.Path} is a copy of the Proving Sample's {original.Path}; pack it from the sample instead.";

    private static string Normalized(string text) => text.ReplaceLineEndings("\n").Trim();

    /// <summary>
    /// The monolith runs without Modularity: no file it generates names the package or one of its public types or
    /// registrations, in a project file or in code.
    /// </summary>
    public static IEnumerable<string> ModularityIn(IEnumerable<TextFile> generated) =>
        from file in generated
        from name in ModularityNames
        where Regex.IsMatch(file.Text, $@"(?<!\w){Regex.Escape(name)}(?!\w)")
        select $"{file.Path} names {name}, but the monolith composes no Modularity.";

    private static readonly string[] ModularityNames =
    [
        typeof(IModuleDefinition).Assembly.GetName().Name!,
        .. typeof(IModuleDefinition)
            .Assembly.GetExportedTypes()
            .Where(type => !type.Name.StartsWith('<'))
            .SelectMany(type =>
                type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(method => !method.IsSpecialName)
                    .Select(method => method.Name)
                    .Prepend(type.Name.Split('`')[0])
            )
            .Distinct()
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// Every sample file the monolith excludes, a Composition Root or Gateway endpoint, has the monolith's own file at
    /// the same path, and the monolith has no file there that replaces nothing. A new Gateway endpoint in the sample
    /// fails here, not in a generated build that would silently lack its route. <paramref name="unreplaced"/> are the
    /// excluded files the monolith needs no replacement for.
    /// </summary>
    public static IEnumerable<string> UnreplacedExclusions(
        IReadOnlyList<string> excluded,
        IReadOnlyList<string> replacements,
        IReadOnlyList<string> unreplaced
    ) =>
        excluded
            .Except(replacements)
            .Except(unreplaced)
            .Select(path => $"The monolith excludes the sample's {path} but has no file of its own there.")
            .Concat(replacements.Except(excluded).Select(path => $"The monolith's {path} replaces no file it excludes from the sample."));

    /// <summary>
    /// A template's project references what the sample's project at the same place references, Kit project references
    /// read as the package of the same name, but for what <paramref name="dropped"/> names.
    /// </summary>
    public static IEnumerable<string> ReferenceDifferences(string path, string template, string sample, params string[] dropped)
    {
        var expected = References(sample).Except(dropped).ToHashSet();
        var actual = References(template);
        return actual
            .Except(expected)
            .Select(reference => $"{path} references {reference}, which the sample's project does not.")
            .Concat(expected.Except(actual).Select(reference => $"{path} does not reference {reference}, which the sample's project does."))
            .Order(StringComparer.Ordinal);
    }

    private static HashSet<string> References(string project) =>
        [
            .. XDocument
                .Parse(project)
                .Descendants()
                .Select(element =>
                    (element.Name.LocalName, Include: element.Attribute("Include")?.Value.Replace('\\', '/')) switch
                    {
                        ("PackageReference" or "FrameworkReference", { } include) => include,
                        ("ProjectReference", { } include) when include.Contains("/src/PouryaKit.", StringComparison.Ordinal) =>
                            Path.GetFileNameWithoutExtension(include),
                        ("ProjectReference", { } include) => "project " + Path.GetFileNameWithoutExtension(include),
                        _ => null,
                    }
                )
                .OfType<string>(),
        ];

    /// <summary>
    /// A template's central package versions: a third-party package has the version the Kit itself builds and tests
    /// with, and a Kit package the one version the package is packed with, <c>$(PouryaKitVersion)</c>, whose value is
    /// the placeholder <paramref name="token"/> that packing replaces.
    /// </summary>
    public static IEnumerable<string> VersionDifferences(string path, string template, string kit, string token)
    {
        var kitVersions = PackageVersions(kit);
        var document = XDocument.Parse(template);
        if (document.Descendants("PouryaKitVersion").SingleOrDefault()?.Value != token)
        {
            yield return $"{path} does not hold the placeholder {token} that packing replaces with the Kit's version.";
        }

        foreach (var (id, version) in PackageVersions(template))
        {
            if (id.StartsWith("PouryaKit.", StringComparison.Ordinal))
            {
                if (version != "$(PouryaKitVersion)")
                {
                    yield return $"{path} pins {id} at {version}; every Kit package has $(PouryaKitVersion).";
                }
            }
            else if (kitVersions.GetValueOrDefault(id) != version)
            {
                yield return $"{path} pins {id} at {version}; the Kit has {kitVersions.GetValueOrDefault(id) ?? "no such package"}.";
            }
        }
    }

    private static Dictionary<string, string> PackageVersions(string props) =>
        XDocument
            .Parse(props)
            .Descendants("PackageVersion")
            .ToDictionary(element => element.Attribute("Include")!.Value, element => element.Attribute("Version")!.Value);
}
