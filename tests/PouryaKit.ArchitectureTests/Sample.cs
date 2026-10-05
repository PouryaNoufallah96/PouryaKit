using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyModel;
using ModularMonolith.Host;
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
        .. DependencyContext.Default!.RuntimeLibraries.Where(library => library.Type == "project").Select(library => library.Name),
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
            .Where(assembly => Modules.Any(module => assembly.GetName().Name == $"{module.GetName().Name}.Contracts"))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal),
    ];

    /// <summary>The Kit's packages, one per folder under <c>src/</c>.</summary>
    public static readonly IReadOnlyList<Assembly> KitPackages =
    [
        .. Directory
            .GetDirectories(Path.Combine(Repository.Root, "src"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .Select(name => Assembly.Load(name!)),
    ];

    public static bool IsModule(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Any(reference => reference.Name == typeof(IModuleDefinition).Assembly.GetName().Name)
        && assembly.GetTypes().Any(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IModuleDefinition)));

    /// <summary>
    /// Each Module's EF Core models, from the Host built (not started) as it composes itself: naming conventions and
    /// schemas come from the Composition Root, so the tables are the ones the database has. Nothing connects.
    /// </summary>
    public static readonly IReadOnlyDictionary<Assembly, IReadOnlyList<IModel>> Models = BuildModels();

    private static Dictionary<Assembly, IReadOnlyList<IModel>> BuildModels()
    {
        var builder = ModularMonolithHost.CreateBuilder(
            ["--environment", "Production", "--ConnectionStrings:app=Host=localhost;Database=architecture-tests"]
        );
        using var host = ModularMonolithHost.Build(builder);
        using var scope = host.Services.CreateScope();
        return Modules.ToDictionary(
            module => module,
            IReadOnlyList<IModel> (module) =>
            [
                .. module
                    .GetTypes()
                    .Where(type => type is { IsAbstract: false } && type.IsAssignableTo(typeof(DbContext)))
                    .Select(type => ((DbContext)scope.ServiceProvider.GetRequiredService(type)).GetService<IDesignTimeModel>().Model),
            ]
        );
    }
}

internal static class Repository
{
    /// <summary>The folder that holds <c>PouryaKit.slnx</c>, found upwards from the test's output.</summary>
    public static readonly string Root = Find(AppContext.BaseDirectory);

    private static string Find(string folder) =>
        File.Exists(Path.Combine(folder, "PouryaKit.slnx"))
            ? folder
            : Find(Directory.GetParent(folder)?.FullName ?? throw new InvalidOperationException("PouryaKit.slnx was not found."));
}
