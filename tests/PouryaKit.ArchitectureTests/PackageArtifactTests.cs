using System.IO.Compression;
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
    [InlineData("THIRD-PARTY-NOTICES.md", "does not embed the third-party notices")]
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
        foreach (var file in new[] { "LICENSE", "THIRD-PARTY-NOTICES.md" })
        {
            using var writer = new StreamWriter(archive.CreateEntry(file).Open());
            writer.Write(File.ReadAllText(Path.Combine(Repository.Root, file)));
        }
        Assert.Empty(PackageArtifacts.MetadataViolations(nuspec, archive, "PouryaKit.Cqrs", evidence));
        if (defect is "LICENSE" or "THIRD-PARTY-NOTICES.md") archive.GetEntry(defect)!.Delete();
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
}
