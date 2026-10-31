using System.Reflection;
using System.Text.Json;
using Inventory.Host;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using ModularMonolith.Host;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;

namespace PouryaKit.ArchitectureTests;

/// <summary>
/// The Proving Sample as the rules see it: the Modules its Host composes, found from the Host's references, their
/// Contracts, the Kit's packages, and each Module's EF Core model as its Composition Root configures it.
/// </summary>
internal static class Sample
{
    private static readonly HashSet<string> ProjectAssemblies =
    [
        .. DependencyContext
            .Default!.RuntimeLibraries.Where(library => library.Type == "project")
            .Select(library => library.Name),
    ];

    /// <summary>Every assembly of this repository's projects that <paramref name="root"/> references, directly or not.</summary>
    public static IReadOnlyList<Assembly> ProjectReferencesOf(Assembly root)
    {
        List<Assembly> found = [];
        Queue<Assembly> next = new([root]);
        HashSet<string> seen = [root.GetName().Name!];
        while (next.TryDequeue(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (ProjectAssemblies.Contains(reference.Name!) && seen.Add(reference.Name!))
                {
                    var loaded = Assembly.Load(reference);
                    found.Add(loaded);
                    next.Enqueue(loaded);
                }
            }
        }

        return found;
    }

    /// <summary>The assemblies the Host references that hold a Module's Composition Root.</summary>
    public static readonly IReadOnlyList<Assembly> Modules =
    [
        .. ProjectReferencesOf(typeof(ModularMonolithHost).Assembly)
            .Where(IsModule)
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal),
    ];

    /// <summary>Each Module's Contracts: the project named after it with <c>.Contracts</c>, when it has one.</summary>
    public static readonly IReadOnlyList<Assembly> Contracts =
    [
        .. ProjectReferencesOf(typeof(ModularMonolithHost).Assembly)
            .Where(assembly =>
                Modules.Any(module =>
                    assembly.GetName().Name == $"{module.GetName().Name}.Contracts"
                )
            )
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The Proving Sample's application code: its two Hosts and every project of this repository they reference that is
    /// no Kit package, so each Module, its Contracts and the shared service defaults.
    /// </summary>
    public static readonly IReadOnlyList<Assembly> Application =
    [
        .. new[] { typeof(ModularMonolithHost).Assembly, typeof(InventoryHost).Assembly }
            .SelectMany(host => ProjectReferencesOf(host).Prepend(host))
            .Where(assembly =>
                !assembly.GetName().Name!.StartsWith("PouryaKit", StringComparison.Ordinal)
            )
            .DistinctBy(assembly => assembly.GetName().Name)
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal),
    ];

    /// <summary>
    /// The restored project of each application project the Host references, by assembly name, as the Host's own
    /// restore found it: a reference that no emitted code uses is still in it.
    /// </summary>
    private static readonly Dictionary<string, string> ProjectFiles = HostProjectFiles();

    public static RestoredPackage RestoredOf(Assembly assembly) =>
        RestoredPackage.Of(
            ProjectFiles.GetValueOrDefault(assembly.GetName().Name!)
                ?? throw new InvalidOperationException(
                    $"{assembly.GetName().Name} is no project the Host restores."
                )
        );

    private static Dictionary<string, string> HostProjectFiles()
    {
        var host = Path.Combine(
            Repository.Root,
            "samples",
            "ModularMonolith",
            "src",
            "ModularMonolith.Host"
        );
        using var assets = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(host, "obj", "project.assets.json"))
        );
        return assets
            .RootElement.GetProperty("libraries")
            .EnumerateObject()
            .Where(library => library.Value.GetProperty("type").GetString() == "project")
            .ToDictionary(
                library => library.Name.Split('/')[0],
                library =>
                    Path.GetFullPath(
                        Path.Combine(host, library.Value.GetProperty("path").GetString()!)
                    )
            );
    }

    /// <summary>The Kit's packages of code, one per folder under <c>src/</c>: every package but the templates, which has none.</summary>
    public static readonly IReadOnlyList<Assembly> KitPackages =
    [
        .. Directory
            .GetDirectories(Path.Combine(Repository.Root, "src"))
            .Select(Path.GetFileName)
            .Where(name => name != "PouryaKit.Templates")
            .Order(StringComparer.Ordinal)
            .Select(name => Assembly.Load(name!)),
    ];

    public static bool IsModule(Assembly assembly) =>
        assembly
            .GetReferencedAssemblies()
            .Any(reference => reference.Name == typeof(IModuleDefinition).Assembly.GetName().Name)
        && assembly
            .GetTypes()
            .Any(type =>
                type is { IsClass: true, IsAbstract: false }
                && type.IsAssignableTo(typeof(IModuleDefinition))
            );

    /// <summary>
    /// Each Module's EF Core models, from the Host built (not started) as it composes itself: naming conventions and
    /// schemas come from the Composition Root, so the tables are the ones the database has. Nothing connects.
    /// </summary>
    public static readonly IReadOnlyDictionary<Assembly, IReadOnlyList<IModel>> Models;

    /// <summary>
    /// The Modules whose Composition Root declares them tenant-scoped, as the same Host records it
    /// (<see cref="HostComposition.IsTenantScoped"/>).
    /// </summary>
    public static readonly IReadOnlyList<Assembly> TenantScopedModules;

    static Sample()
    {
        var builder = ModularMonolithHost.CreateBuilder([
            "--environment",
            "Production",
            "--ConnectionStrings:app=Host=localhost;Database=architecture-tests",
        ]);
        using var host = ModularMonolithHost.Build(builder);
        using var scope = host.Services.CreateScope();
        Models = Modules.ToDictionary(
            module => module,
            IReadOnlyList<IModel> (module) =>
                [
                    .. module
                        .GetTypes()
                        .Where(type =>
                            type is { IsAbstract: false } && type.IsAssignableTo(typeof(DbContext))
                        )
                        .Select(type =>
                            ((DbContext)scope.ServiceProvider.GetRequiredService(type))
                                .GetService<IDesignTimeModel>()
                                .Model
                        ),
                ]
        );
        var composition = host.Services.GetRequiredService<HostComposition>();
        TenantScopedModules =
        [
            .. Modules.Where(module => composition.IsTenantScoped(DefinitionOf(module))),
        ];
    }

    private static Type DefinitionOf(Assembly module) =>
        module
            .GetTypes()
            .Single(type =>
                type is { IsClass: true, IsAbstract: false }
                && type.IsAssignableTo(typeof(IModuleDefinition))
            );
}

internal static class Repository
{
    /// <summary>The folder that holds <c>PouryaKit.slnx</c>, found upwards from the test's output.</summary>
    public static readonly string Root = Find(AppContext.BaseDirectory);

    private static string Find(string folder) =>
        File.Exists(Path.Combine(folder, "PouryaKit.slnx"))
            ? folder
            : Find(
                Directory.GetParent(folder)?.FullName
                    ?? throw new InvalidOperationException("PouryaKit.slnx was not found.")
            );
}
