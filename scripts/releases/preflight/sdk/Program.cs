using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.DotNet.ApiSymbolExtensions;
using Microsoft.DotNet.ApiSymbolExtensions.Logging;

var log = new GuardLog();
try
{
    var inputs = XDocument.Load(args[0]);
    foreach (var side in new[] { "Candidate", "Baseline" })
    {
        var package = inputs.Descendants(side).Single().Value;
        if (package.Length == 0)
            continue;
        using var archive = ZipFile.OpenRead(package);
        var groups = archive.Entries.Where(entry => entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !entry.FullName.Contains("/native/", StringComparison.Ordinal))
            .GroupBy(entry => AssetGroup(entry.FullName)).ToArray();
        if (groups.Length == 0)
            throw new InvalidDataException($"Package has no managed assets: {package}");
        foreach (var group in groups)
        {
            var tfm = group.Key.Split('/')[^1];
            if (tfm != "net10.0")
                throw new InvalidDataException($"Unsupported managed asset group: {group.Key}");
            var references = inputs.Descendants(side + "References")
                .Where(item => item.Element("TargetFramework")?.Value == tfm)
                .SelectMany(item => item.Element("ReferencePath")!.Value.Split(',')).ToArray();
            if (references.Length == 0 || references.Any(path => !File.Exists(path)))
                throw new InvalidDataException($"Missing assembly reference inputs: {side}/{tfm}");
            var loader = new AssemblySymbolLoader(log, resolveAssemblyReferences: true);
            loader.AddReferenceSearchPaths(references);
            var symbols = loader.LoadAssembliesFromArchive(package, group.Select(entry => entry.FullName).ToArray());
            if (symbols.Count != group.Count() || symbols.Any(symbol => symbol is null))
                log.LogError($"No usable symbols for every asset: {package}/{group.Key}");
        }
    }
}
catch (Exception error)
{
    log.LogError(error.ToString());
}
Console.WriteLine("reference-complete=" + !log.HasLoggedErrors);
return log.HasLoggedErrors ? 2 : 0;

static string AssetGroup(string name)
{
    var parts = name.Split('/');
    if (parts.Length == 3 && parts[0] is "lib" or "ref")
        return string.Join('/', parts[..2]);
    if (parts.Length == 5 && parts[0] == "runtimes" && parts[2] == "lib")
        return string.Join('/', parts[..4]);
    throw new InvalidDataException($"Unsupported managed asset path: {name}");
}

sealed class GuardLog : ILog
{
    public bool HasLoggedErrors { get; private set; }
    public void LogError(string message) { HasLoggedErrors = true; Console.WriteLine(message); }
    public void LogError(string code, string message) => LogError(code + ": " + message);
    public void LogWarning(string message) => LogError(message);
    public void LogWarning(string code, string message) => LogError(code + ": " + message);
    public void LogMessage(string message) => Console.WriteLine(message);
    public void LogMessage(MessageImportance importance, string message)
    {
        if (importance == MessageImportance.High)
            LogError(message);
    }
}
