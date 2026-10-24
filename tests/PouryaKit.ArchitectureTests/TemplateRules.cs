using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using PouryaKit.AI;
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
    /// Module's C# files, the Host's permission source, announcer and guardrail, and the service defaults.
    /// </summary>
    public static IReadOnlyList<TextFile> PackedSample { get; } =
        [
            .. Files(SampleRoot)
                .Where(file =>
                    (file.Path.StartsWith("Modules/", StringComparison.Ordinal) && file.Path.EndsWith(".cs", StringComparison.Ordinal))
                    || file.Path
                        is "ModularMonolith.Host/CallerPermissions.cs"
                            or "ModularMonolith.Host/LoggedNewProductAnnouncer.cs"
                            or "ModularMonolith.Host/CardNumberGuardrail.cs"
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
    /// registrations, in a project file or in code. Its test project reaches Modularity only through PouryaKit.Testing,
    /// which <c>validate.py</c> allows in that project's closure alone (ticket #87).
    /// </summary>
    public static IEnumerable<string> ModularityIn(IEnumerable<TextFile> generated) => Naming(generated, ModularityNames, "Modularity");

    /// <summary>
    /// The monolith runs without AI either: no file it generates names PouryaKit.AI, one of its public types or
    /// registrations, or the Agent Framework it brings, so the sample's agent and the Host's guardrail stay out of it
    /// (ticket #173).
    /// </summary>
    public static IEnumerable<string> AIIn(IEnumerable<TextFile> generated) => Naming(generated, AINames, "AI");

    private static IEnumerable<string> Naming(IEnumerable<TextFile> generated, string[] names, string capability) =>
        from file in generated
        from name in names
        where Regex.IsMatch(file.Text, $@"(?<!\w){Regex.Escape(name)}(?!\w)")
        select $"{file.Path} names {name}, but the monolith composes no {capability}.";

    private static readonly string[] ModularityNames = NamesOf(typeof(IModuleDefinition).Assembly);

    private static readonly string[] AINames = [.. NamesOf(typeof(IChatGuardrail).Assembly), "Microsoft.Agents.AI"];

    // The package's name, and each public type's and each public static method's, such as a registration.
    private static string[] NamesOf(Assembly package) =>
        [
            package.GetName().Name!,
            .. package
                .GetExportedTypes()
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
    /// Each of the monolith's own files that replaces a sample file makes the sample file's calls, in its order and with
    /// its arguments, so a route, an API version, an anonymous Actor, an Idempotency-Key, an endpoint name or a
    /// registration that changes in the sample fails here until the monolith follows (ticket #87). A call is a member
    /// call or an object creation, its arguments as written with any nested parentheses read as <c>(…)</c>, since each
    /// nested call is compared on its own; a constant, such as an endpoint's name, is compared with its value. Read away
    /// first are the composition's own differences: a sample endpoint passes the Operation Context to the Gateway, which
    /// the monolith's <c>IDispatcher</c> takes from the request; only the sample registers <c>ICatalogProducts</c>, which
    /// answers other Modules; and the monolith names its assembly, <c>catalog</c>, where a Module's registration infers
    /// it. A type argument is read by its last name, as a file's usings decide how much of it is written.
    /// </summary>
    public static IEnumerable<string> MirrorDifferences(IEnumerable<TextFile> replacements, IReadOnlyList<TextFile> sample)
    {
        foreach (var replacement in replacements)
        {
            var original = sample.Single(file => "src/" + file.Path == replacement.Path);
            List<string> calls = Calls(replacement.Text), expected = Calls(original.Text);
            var at = Enumerable.Range(0, Math.Max(calls.Count, expected.Count)).FirstOrDefault(i => calls.ElementAtOrDefault(i) != expected.ElementAtOrDefault(i), -1);
            if (at >= 0)
            {
                yield return $"{replacement.Path} calls {calls.ElementAtOrDefault(at) ?? "nothing more"} where the sample's {original.Path} calls {expected.ElementAtOrDefault(at) ?? "nothing more"}.";
            }
        }
    }

    private static List<string> Calls(string text)
    {
        text = Regex.Replace(text, "//.*", "");
        text = Regex.Replace(text, @",\s*context(?=\s*[,)])", "");
        text = Regex.Replace(Regex.Replace(text, @"\(\s*catalog\s*\)", "()"), @",\s*catalog(?=\s*\))", "");
        var constants = Regex.Matches(text, @"\bconst\s+\w+\s+(\w+\s*=\s*[^;]*)").Select(match => (match.Index, Call: "const " + Spaced(match.Groups[1].Value)));
        var calls = Regex
            .Matches(text, @"(?:\.|\b(?<new>new)\s+)(?<name>\w+)\s*(?<types><[^()]*?>)?\s*\(")
            .Select(match =>
                (
                    match.Index,
                    Call: (match.Groups["new"].Success ? "new " : "")
                        + match.Groups["name"].Value
                        + Spaced(Regex.Replace(match.Groups["types"].Value, @"(?:\w+\.)+(\w+)", "$1"))
                        + Arguments(text, match.Index + match.Length - 1)
                )
            );
        return [.. constants.Concat(calls).OrderBy(call => call.Index).Select(call => call.Call).Where(call => call != "AddScoped<ICatalogProducts, CatalogProducts>()")];
    }

    // The arguments of the call whose parenthesis opens at open, each nested parenthesis read as (…).
    private static string Arguments(string text, int open)
    {
        var arguments = new System.Text.StringBuilder();
        var depth = 0;
        for (var i = open; ; i++)
        {
            depth += text[i] switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0,
            };
            if (depth == 0)
            {
                return Spaced(arguments.Append(')').ToString());
            }

            arguments.Append(depth switch
            {
                1 => text[i] == ')' ? ")" : text[i].ToString(),
                2 when text[i] == '(' => "(…",
                _ => "",
            });
        }
    }

    private static string Spaced(string text) => Regex.Replace(Regex.Replace(text, @"\s+", " "), @"(?<=\()\s|\s(?=\))", "");

    /// <summary>
    /// A template Host builds its request pipeline as the Proving Sample's Host does: the same <c>app.Use…</c> and
    /// <c>app.Map…</c> calls in the same order, with <c>MapDefaultEndpoints</c> allowed, as only a generated Host has the
    /// service defaults' endpoints. Middleware runs in the order it is added, so a template Host whose order or middleware
    /// differs serves differently from the Host the Kit's tests run (ticket #87).
    /// </summary>
    public static IEnumerable<string> PipelineDifferences(TextFile host, TextFile sample)
    {
        var expected = Pipeline(sample.Text);
        var actual = Pipeline(host.Text).Where(call => call != "MapDefaultEndpoints").ToList();
        if (!actual.SequenceEqual(expected))
        {
            yield return $"{host.Path} builds its pipeline with {string.Join(", ", actual)}; the sample's {sample.Path} with {string.Join(", ", expected)}.";
        }
    }

    private static List<string> Pipeline(string text) =>
        [.. Regex.Matches(Regex.Replace(text, "//.*", ""), @"\bapp\.((?:Use|Map)\w*)\s*\(").Select(match => match.Groups[1].Value)];

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

    /// <summary>
    /// The token <paramref name="template"/>'s derived <paramref name="symbol"/> replaces with a value it derives from the
    /// project's name, and the symbol and the forms it uses, as written: <c>serviceName</c> a queue name, <c>projectName</c>
    /// the name as typed.
    /// </summary>
    public static (string Token, string Derivation) DerivedSymbol(string template, string symbol)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(Content, template, ".template.config", "template.json")));
        var derived = configuration.RootElement.GetProperty("symbols").GetProperty(symbol);
        return (derived.GetProperty("replaces").GetString()!, derived.GetRawText() + configuration.RootElement.GetProperty("forms").GetRawText());
    }

    /// <summary>The name <paramref name="template"/>'s files carry, which the engine replaces with the project's name.</summary>
    public static string SourceName(string template)
    {
        using var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(Content, template, ".template.config", "template.json")));
        return configuration.RootElement.GetProperty("sourceName").GetString()!;
    }

    /// <summary>
    /// A template's solution, project and launch settings files name a project's path through <paramref name="token"/>,
    /// which the template's <c>projectName</c> symbol replaces with the name as typed, the name the engine gives the files
    /// and folders; never through <paramref name="sourceName"/>, which the engine writes in content as the name a
    /// namespace can hold, so <c>-n acme-shop</c> would name <c>acme_shop.Host</c> beside the folder <c>acme-shop.Host</c>.
    /// Each path in the solution or a project reference names a project the template generates, the token read as the
    /// <paramref name="sourceName"/> the files carry.
    /// </summary>
    public static IEnumerable<string> ProjectPathDefects(IReadOnlyList<TextFile> generated, string sourceName, string token)
    {
        var projects = generated.Select(file => file.Path).Where(path => path.EndsWith(".csproj", StringComparison.Ordinal)).ToHashSet();
        foreach (var file in generated.Where(file => Path.GetExtension(file.Path) is ".slnx" or ".csproj" || file.Path.EndsWith("/launchSettings.json", StringComparison.Ordinal)))
        {
            if (file.Text.Contains(sourceName, StringComparison.Ordinal))
            {
                yield return $"{file.Path} spells the sourceName {sourceName}, which the engine writes as the name a namespace can hold (acme_shop for -n acme-shop), not as it names the files; name a project's path through {token}.";
            }

            if (file.Path.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            var paths = XDocument
                .Parse(file.Text)
                .Descendants()
                .Select(element => element.Name.LocalName switch
                {
                    "Project" => element.Attribute("Path")?.Value,
                    "ProjectReference" => element.Attribute("Include")?.Value,
                    _ => null,
                })
                .OfType<string>();
            foreach (var path in paths.Where(path => !projects.Contains(Resolved(Path.GetDirectoryName(file.Path)!, path.Replace(token, sourceName, StringComparison.Ordinal)))))
            {
                yield return $"{file.Path} names {path}, which is no project the template generates.";
            }
        }
    }

    // A relative path from a generated file's folder, as a generated path with forward slashes.
    private static string Resolved(string folder, string relative)
    {
        List<string> parts = [];
        foreach (var part in $"{folder}/{relative}".Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>
    /// A generated Host's queue on a broker is the template's derived <c>serviceName</c>, <paramref name="token"/> in the
    /// content, never another literal: the <c>sourceName</c> becomes the project's name as the engine writes it in code,
    /// such as <c>acme_shop</c>, which is no valid queue name, and a Host given one fails as it is composed.
    /// </summary>
    public static IEnumerable<string> UnderivedServiceNames(IEnumerable<TextFile> generated, string token) =>
        from file in generated
        where file.Path.EndsWith(".cs", StringComparison.Ordinal)
        from match in Regex.Matches(file.Text, "serviceName: \"([^\"]*)\"")
        where match.Groups[1].Value != token
        select $"{file.Path} passes the service name {match.Groups[1].Value}; pass {token}, which the template's serviceName symbol derives into a valid queue name from any project name.";

    /// <summary>
    /// A problem for each generated file that calls <c>UseRouting()</c> after <c>UseRateLimiter()</c>. The rate limiting
    /// middleware reads the matched endpoint, so placed before routing it ignores the exemption of the service
    /// defaults' <c>/alive</c> and the Actor an anonymous endpoint names, and the Kit cannot tell that order from a
    /// request no endpoint matches.
    /// </summary>
    public static IEnumerable<string> RateLimitersBeforeRouting(IEnumerable<TextFile> generated) =>
        from file in generated
        let limiter = file.Text.IndexOf("app.UseRateLimiter()", StringComparison.Ordinal)
        where limiter >= 0 && file.Text.IndexOf("app.UseRouting()", limiter, StringComparison.Ordinal) >= 0
        select $"{file.Path} calls UseRouting() after UseRateLimiter(); route first, or the rate limit ignores the exemption of /alive and the Actor an anonymous endpoint names.";

    /// <summary>
    /// A problem for each of a template's <paramref name="own"/> C# files that takes a repository: <c>IRepository</c>, or
    /// an interface <paramref name="sample"/> derives from it. A file takes one when it declares a parameter, a field or
    /// a local of that type, or asks a service provider for it. In an application without Modules the request scope has
    /// a Unit of Work, so such a file can load an aggregate and change it outside a command, where no call of the Kit
    /// is made to refuse it (ADR 0003, ticket #68). The business code comes from the sample and is not judged here, so
    /// this rule never has to tell a handler from a service that a handler calls. A registration names the repository
    /// as a type argument and takes nothing; <c>//</c> comments are left out. It cannot see a repository reached through
    /// an alias, through <c>typeof</c>, inside another generic type such as <c>IEnumerable</c>, or through another type
    /// the file takes that itself takes one.
    /// </summary>
    public static IEnumerable<string> RepositoriesTaken(IEnumerable<TextFile> own, IEnumerable<TextFile> sample)
    {
        var derived = sample.SelectMany(file => Regex.Matches(file.Text, @"(?<!\w)interface\s+(\w+)\s*:[^{]*?(?<!\w)IRepository<")).Select(match => match.Groups[1].Value);
        var repository = string.Join('|', ["IRepository<[^<>]*>", .. derived.Distinct().Select(Regex.Escape)]);
        var taken = new Regex($@"(?<!\w)(?<taken>{repository})\??\s+@?[A-Za-z_]|Service<\s*(?<taken>{repository})\s*>");
        return from file in own
            where file.Path.EndsWith(".cs", StringComparison.Ordinal)
            from name in taken.Matches(Regex.Replace(file.Text, "//.*", "")).Select(match => match.Groups["taken"].Value).Distinct()
            select $"{file.Path} takes {name}; send a command through IDispatcher instead: a handler takes the repository, and a change made outside a command is dropped or saved by a command that never made it.";
    }

    private static Dictionary<string, string> PackageVersions(string props) =>
        XDocument
            .Parse(props)
            .Descendants("PackageVersion")
            .ToDictionary(element => element.Attribute("Include")!.Value, element => element.Attribute("Version")!.Value);
}
