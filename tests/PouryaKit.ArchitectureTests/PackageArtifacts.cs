using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Xml.Linq;

namespace PouryaKit.ArchitectureTests;

internal sealed record ConsumerEvidence(string Name, string[] Packages, string[] Assets);
internal sealed record PackageEvidence(string Directory, string Version, string Commit, string[] Packages, ConsumerEvidence[] Consumers);

internal static class PackageArtifacts
{
    internal const string RepositoryUrl = "https://github.com/PouryaNoufallah96/PouryaKit";
    private static readonly Guid SourceLinkId = new("CC110556-A091-4D38-9FEC-25AB9A351A6A");

    public static IEnumerable<string> Violations(string evidenceFile)
    {
        var evidence = JsonSerializer.Deserialize<PackageEvidence>(File.ReadAllText(evidenceFile))!;
        if (evidence.Packages.Length == 0 || evidence.Packages.Distinct().Count() != evidence.Packages.Length)
            throw new InvalidOperationException("The expected package inventory must be nonempty and unique.");
        var files = System.IO.Directory.GetFiles(evidence.Directory, "*.nupkg");
        var nuspecs = new Dictionary<string, XDocument>();
        foreach (var file in files)
        {
            using var archive = ZipFile.OpenRead(file);
            using var manifest = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var nuspec = XDocument.Load(manifest);
            var name = Value(nuspec, "id");
            if (!nuspecs.TryAdd(name, nuspec)) yield return $"Duplicate package {name}.";
            if (Path.GetFileName(file) != $"{name}.{evidence.Version}.nupkg") yield return $"{name} has an unexpected artifact filename.";
            foreach (var error in MetadataViolations(nuspec, archive, name, evidence)) yield return error;
            foreach (var error in PackageGraph.Violations(RestoredPackage.FromNuspec(nuspec))) yield return error;
            // A template package carries content, no assembly to link to its source.
            if (!IsTemplate(nuspec))
                foreach (var error in SourceLinkViolations(archive, name, evidence.Commit, evidence.Version)) yield return error;
        }
        if (!nuspecs.Keys.ToHashSet().SetEquals(evidence.Packages))
        {
            yield return "Produced packages differ from the explicit inventory.";
            yield break;
        }

        foreach (var consumer in evidence.Consumers)
        {
            foreach (var asset in consumer.Assets)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(asset));
                var libraries = document.RootElement.GetProperty("libraries").EnumerateObject().ToArray();
                foreach (var library in libraries.Where(library => library.Name.StartsWith("PouryaKit.", StringComparison.Ordinal)))
                {
                    var parts = library.Name.Split('/');
                    if (!evidence.Packages.Contains(parts[0]) || parts[1] != evidence.Version
                        || library.Value.GetProperty("type").GetString() != "package")
                        yield return $"{consumer.Name} resolves an unexpected Kit dependency {library.Name}.";
                    var restored = RestoredPackage.InConsumer(nuspecs[parts[0]], asset);
                    foreach (var error in PackageGraph.Violations(restored)) yield return error;
                    foreach (var error in LicenceTests.Violations(restored.Packages)) yield return error;
                }
            }
            var arrived = consumer.Assets.SelectMany(asset =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(asset));
                return document.RootElement.GetProperty("libraries").EnumerateObject()
                    .Select(library => library.Name.Split('/')[0]).ToArray();
            }).ToHashSet();
            foreach (var expected in consumer.Packages.Where(package => !arrived.Contains(package)))
                yield return $"{consumer.Name} did not restore {expected}.";
        }
    }

    internal static IEnumerable<string> MetadataViolations(XDocument nuspec, ZipArchive archive, string name, PackageEvidence evidence)
    {
        if (Value(nuspec, "version") != evidence.Version) yield return $"{name} has the wrong version.";
        var repository = nuspec.Descendants().SingleOrDefault(element => element.Name.LocalName == "repository");
        if (repository?.Attribute("type")?.Value != "git" || repository?.Attribute("url")?.Value != RepositoryUrl
            || repository?.Attribute("commit")?.Value != evidence.Commit)
            yield return $"{name} has invalid repository/source commit metadata.";
        var license = nuspec.Descendants().SingleOrDefault(element => element.Name.LocalName == "license");
        if (license?.Attribute("type")?.Value != "file" || license?.Value != "LICENSE"
            || !Matches(archive, "LICENSE", Path.Combine(Repository.Root, "LICENSE")))
            yield return $"{name} does not embed the Kit's MIT license.";
        foreach (var dependency in nuspec.Descendants().Where(element => element.Name.LocalName == "dependency"
            && element.Attribute("id")!.Value.StartsWith("PouryaKit.", StringComparison.Ordinal)))
        {
            if (dependency.Attribute("version")?.Value != evidence.Version)
                yield return $"{name} has a Kit dependency with a different version.";
        }
    }

    private static bool Matches(ZipArchive archive, string entryName, string source)
    {
        var entry = archive.GetEntry(entryName);
        if (entry is null) return false;
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd() == File.ReadAllText(source);
    }

    internal static IEnumerable<string> SourceLinkViolations(ZipArchive archive, string name, string commit, string? version = null)
    {
        var assembly = archive.GetEntry($"lib/net10.0/{name}.dll");
        if (assembly is null) { yield return $"{name} has no net10.0 assembly."; yield break; }
        using var content = new MemoryStream();
        using (var stream = assembly.Open()) stream.CopyTo(content);
        content.Position = 0;
        using var pe = new PEReader(content);
        if (version is not null)
        {
            var assemblyMetadata = pe.GetMetadataReader();
            var informational = assemblyMetadata.GetAssemblyDefinition().GetCustomAttributes()
                .Select(assemblyMetadata.GetCustomAttribute)
                .Where(attribute => attribute.Constructor.Kind == HandleKind.MemberReference)
                .Where(attribute =>
                {
                    var constructor = assemblyMetadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    return constructor.Parent.Kind == HandleKind.TypeReference
                        && assemblyMetadata.GetString(assemblyMetadata.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name)
                            == "AssemblyInformationalVersionAttribute";
                }).Select(attribute =>
                {
                    var value = assemblyMetadata.GetBlobReader(attribute.Value);
                    value.ReadUInt16();
                    return value.ReadSerializedString();
                }).SingleOrDefault();
            if (informational != $"{version}+{commit}") yield return $"{name}'s built assembly does not match the package version/source commit.";
        }
        var debug = pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb).ToArray();
        if (debug.Length != 1) { yield return $"{name} has no embedded portable PDB."; yield break; }
        using var provider = pe.ReadEmbeddedPortablePdbDebugDirectoryData(debug[0]);
        var metadata = provider.GetMetadataReader();
        var links = metadata.CustomDebugInformation.Select(metadata.GetCustomDebugInformation)
            .Where(information => metadata.GetGuid(information.Kind) == SourceLinkId).ToArray();
        if (links.Length != 1) { yield return $"{name} has no SourceLink map."; yield break; }
        using var json = JsonDocument.Parse(metadata.GetBlobBytes(links[0].Value));
        var mappings = json.RootElement.GetProperty("documents").EnumerateObject().ToArray();
        var expected = $"https://raw.githubusercontent.com/PouryaNoufallah96/PouryaKit/{commit}/";
        if (mappings.Length == 0 || mappings.Any(mapping => !mapping.Value.GetString()!.StartsWith(expected, StringComparison.Ordinal)))
        {
            yield return $"{name}'s SourceLink map does not identify the source commit.";
            yield break;
        }
        var sources = metadata.Documents.Select(metadata.GetDocument).Select(document => metadata.GetString(document.Name))
            .Where(path => path.Contains($"/src/{name}/", StringComparison.Ordinal) && !path.Contains("/obj/", StringComparison.Ordinal)).ToArray();
        foreach (var error in SourceLinkDocumentViolations(name, sources,
            [.. mappings.Select(mapping => KeyValuePair.Create(mapping.Name, mapping.Value.GetString()!))], expected)) yield return error;
    }

    internal static IEnumerable<string> SourceLinkDocumentViolations(string name, string[] sources,
        KeyValuePair<string, string>[] mappings, string expected)
    {
        if (mappings.Any(mapping => mapping.Key.Count(character => character == '*') > 1
            || (mapping.Key.Contains('*') && !mapping.Key.EndsWith('*'))
            || mapping.Value.Count(character => character == '*') != (mapping.Key.EndsWith('*') ? 1 : 0)))
        {
            yield return $"{name}'s SourceLink map has invalid wildcard syntax.";
            yield break;
        }
        if (sources.Length == 0) yield return $"{name}'s SourceLink map does not cover its source documents.";
        foreach (var source in sources)
        {
            var mapped = mappings.Where(mapping => string.Equals(mapping.Key, source, StringComparison.OrdinalIgnoreCase)
                || (mapping.Key.EndsWith('*') && source.StartsWith(mapping.Key[..^1], StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(mapping => !mapping.Key.EndsWith('*')).ThenByDescending(mapping => mapping.Key.Length).ToArray();
            var marker = source.LastIndexOf($"/src/{name}/", StringComparison.Ordinal);
            var url = mapped.Length > 0 ? mapped[0].Value.Replace("*", mapped[0].Key.EndsWith('*')
                ? source[(mapped[0].Key.Length - 1)..].Replace('\\', '/') : "", StringComparison.Ordinal) : null;
            if (marker < 0 || url != expected + source[(marker + 1)..])
                yield return $"{name}'s SourceLink map sends {source} to the wrong repository-relative path.";
        }
    }

    private static bool IsTemplate(XDocument nuspec) =>
        nuspec.Descendants().Any(element => element.Name.LocalName == "packageType" && element.Attribute("name")?.Value == "Template");

    private static string Value(XDocument document, string name) => document.Descendants().Single(element => element.Name.LocalName == name).Value;
}
