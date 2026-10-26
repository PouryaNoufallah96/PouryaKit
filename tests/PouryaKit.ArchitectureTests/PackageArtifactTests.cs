using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace PouryaKit.ArchitectureTests;

public sealed class PackageArtifactTests
{
    private const string Version = "0.1.0-preview.1";
    private const string Commit = "0123456789012345678901234567890123456789";

    [Theory]
    [InlineData("version", "has the wrong version")]
    [InlineData("repository", "has invalid repository/source commit metadata")]
    [InlineData("LICENSE", "does not embed the Kit's MIT license")]
    [InlineData("dependency", "has a Kit dependency with a different version")]
    public void Invalid_release_metadata_is_rejected(string defect, string expected)
    {
        var nuspec = XDocument.Parse($"""
            <package><metadata><id>PouryaKit.Cqrs</id><version>{Version}</version>
              <license type="file">LICENSE</license>
              <repository type="git" url="{PackageArtifacts.RepositoryUrl}" commit="{Commit}" />
              <dependencies><group><dependency id="PouryaKit.Domain" version="{Version}" /></group></dependencies>
            </metadata></package>
            """);
        var evidence = new PackageEvidence("unused", Version, Commit, ["PouryaKit.Cqrs"], []);
        using var stream = new MemoryStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
        using (var writer = new StreamWriter(archive.CreateEntry("LICENSE").Open()))
            writer.Write(File.ReadAllText(Path.Combine(Repository.Root, "LICENSE")));
        Assert.Empty(PackageArtifacts.MetadataViolations(nuspec, archive, "PouryaKit.Cqrs", evidence));
        if (defect == "LICENSE") archive.GetEntry(defect)!.Delete();
        else if (defect == "version") nuspec.Descendants("version").Single().Value = "0.9.0-preview.1";
        else nuspec.Descendants(defect).Single().SetAttributeValue(defect == "repository" ? "commit" : "version", "wrong");

        Assert.Equal([$"PouryaKit.Cqrs {expected}."],
            PackageArtifacts.MetadataViolations(nuspec, archive, "PouryaKit.Cqrs", evidence));
    }

    [Fact]
    public void Embedded_source_link_identifies_the_commit_and_rejects_a_different_commit()
    {
        using var stream = new MemoryStream();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
        var assembly = Path.Combine(Repository.Root, "src", "PouryaKit.Domain", "bin",
            Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name, "net10.0", "PouryaKit.Domain.dll");
        using (var writer = archive.CreateEntry("lib/net10.0/PouryaKit.Domain.dll").Open())
        using (var input = File.OpenRead(assembly)) input.CopyTo(writer);
        var sourceLink = Directory.GetFiles(Path.Combine(Repository.Root, "src", "PouryaKit.Domain", "obj"), "*.sourcelink.json", SearchOption.AllDirectories)
            .First(file => file.Contains($"/{Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name}/", StringComparison.Ordinal));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(sourceLink));
        var actual = json.RootElement.GetProperty("documents").EnumerateObject().Single().Value.GetString()!.Split('/')[5];

        Assert.Empty(PackageArtifacts.SourceLinkViolations(archive, "PouryaKit.Domain", actual));
        Assert.Equal(["PouryaKit.Domain's SourceLink map does not identify the source commit."],
            PackageArtifacts.SourceLinkViolations(archive, "PouryaKit.Domain", new string('0', 40)));
    }

    [Fact]
    public async Task An_embedded_source_link_map_with_the_right_commit_but_wrong_source_path_is_rejected()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"pouryakit-bad-sourcelink-{Guid.NewGuid():N}");
        try
        {
            var source = Path.Combine(folder, "src", "PouryaKit.Domain");
            Directory.CreateDirectory(source);
            File.Copy(Path.Combine(Repository.Root, "global.json"), Path.Combine(folder, "global.json"));
            var project = Path.Combine(source, "PouryaKit.Domain.csproj");
            await File.WriteAllTextAsync(project, """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
                  <TargetFramework>net10.0</TargetFramework><DebugType>embedded</DebugType>
                  <EnableSourceControlManagerQueries>false</EnableSourceControlManagerQueries><EnableSourceLink>false</EnableSourceLink>
                  <SourceLink>$(MSBuildProjectDirectory)/bad-map.json</SourceLink>
                </PropertyGroup></Project>
                """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(source, "Domain.cs"), "public class Domain { public int Value => 1; }",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(source, "bad-map.json"), JsonSerializer.Serialize(new
            {
                documents = new Dictionary<string, string>
                {
                    ["*"] = $"https://raw.githubusercontent.com/PouryaNoufallah96/PouryaKit/{Commit}/does-not-exist/*",
                },
            }), TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo("dotnet") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "build", project, "-c", "Release", "-v:q" }) start.ArgumentList.Add(argument);
            using var build = Process.Start(start)!;
            var stdout = build.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = build.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await build.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(build.ExitCode == 0, await stdout + await stderr);

            using var stream = new MemoryStream();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
            using (var target = archive.CreateEntry("lib/net10.0/PouryaKit.Domain.dll").Open())
            using (var input = File.OpenRead(Path.Combine(source, "bin", "Release", "net10.0", "PouryaKit.Domain.dll"))) input.CopyTo(target);
            Assert.Contains(PackageArtifacts.SourceLinkViolations(archive, "PouryaKit.Domain", Commit),
                violation => violation.Contains("wrong repository-relative path", StringComparison.Ordinal));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Source_link_expands_wildcards_and_prefers_exact_and_more_specific_mappings()
    {
        const string source = "/_/src/PouryaKit.Domain/Result.cs";
        var expected = $"https://raw.githubusercontent.com/PouryaNoufallah96/PouryaKit/{Commit}/";
        foreach (var mappings in new KeyValuePair<string, string>[][]
        {
            [KeyValuePair.Create("/_/*", expected + "*")],
            [KeyValuePair.Create(source.ToUpperInvariant(), expected + "src/PouryaKit.Domain/Result.cs")],
            [KeyValuePair.Create("/_/*", expected + "wrong/*"), KeyValuePair.Create("/_/src/PouryaKit.Domain/*", expected + "src/PouryaKit.Domain/*")],
            [KeyValuePair.Create("/_/*", expected + "wrong/*"), KeyValuePair.Create(source, expected + "src/PouryaKit.Domain/Result.cs")],
        })
            Assert.Empty(PackageArtifacts.SourceLinkDocumentViolations("PouryaKit.Domain", [source], mappings, expected));
        Assert.Contains(PackageArtifacts.SourceLinkDocumentViolations("PouryaKit.Domain", [source],
            [KeyValuePair.Create("/_/*", expected + "wrong/*")], expected),
            violation => violation.Contains("wrong repository-relative path", StringComparison.Ordinal));
        Assert.Equal(["PouryaKit.Domain's SourceLink map has invalid wildcard syntax."],
            PackageArtifacts.SourceLinkDocumentViolations("PouryaKit.Domain", [source], [KeyValuePair.Create("/_/*", expected + "constant")], expected));
    }

    // A generated solution's test project references its Host, which takes EF Core's design package with
    // PrivateAssets="all"; NuGet then leaves that package out of the test project's graph, though WolverineFx's EF Core
    // integration depends on it (#53).
    [Fact]
    public void A_consumer_closure_reads_the_packages_that_arrived_and_not_a_dependency_the_consumers_graph_left_out()
    {
        var folder = Directory.CreateTempSubdirectory("pouryakit-left-out-").FullName;
        try
        {
            var packages = Path.Combine(folder, "packages") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(Path.Combine(packages, "wolverinefx.entityframeworkcore", "6.46.0"));
            File.WriteAllText(
                Path.Combine(packages, "wolverinefx.entityframeworkcore", "6.46.0", "wolverinefx.entityframeworkcore.nuspec"),
                """<package><metadata><id>WolverineFx.EntityFrameworkCore</id><license type="expression">MIT</license></metadata></package>"""
            );
            var assets = Path.Combine(folder, "project.assets.json");
            File.WriteAllText(assets, JsonSerializer.Serialize(new
            {
                targets = new Dictionary<string, object>
                {
                    ["net10.0"] = new Dictionary<string, object>
                    {
                        ["PouryaKit.Messaging.Wolverine.EFCore/1.0.0"] = new { type = "package", dependencies = new Dictionary<string, string> { ["WolverineFx.EntityFrameworkCore"] = "6.46.0" } },
                        ["WolverineFx.EntityFrameworkCore/6.46.0"] = new { type = "package", dependencies = new Dictionary<string, string> { ["Microsoft.EntityFrameworkCore.Design"] = "10.0.4" } },
                    },
                },
                libraries = new Dictionary<string, object>
                {
                    ["PouryaKit.Messaging.Wolverine.EFCore/1.0.0"] = new { type = "package", path = "pouryakit.messaging.wolverine.efcore/1.0.0", files = new[] { "pouryakit.messaging.wolverine.efcore.nuspec" } },
                    ["WolverineFx.EntityFrameworkCore/6.46.0"] = new { type = "package", path = "wolverinefx.entityframeworkcore/6.46.0", files = new[] { "wolverinefx.entityframeworkcore.nuspec" } },
                },
                packageFolders = new Dictionary<string, object> { [packages] = new { } },
            }));
            var shipped = XDocument.Parse(
                """<package><metadata><id>PouryaKit.Messaging.Wolverine.EFCore</id><dependencies><group targetFramework="net10.0"><dependency id="WolverineFx.EntityFrameworkCore" version="6.46.0" /></group></dependencies></metadata></package>"""
            );

            var restored = RestoredPackage.InConsumer(shipped, assets);

            Assert.Equal([new PackageLicence("WolverineFx.EntityFrameworkCore", "6.46.0", "MIT")], restored.Packages);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
