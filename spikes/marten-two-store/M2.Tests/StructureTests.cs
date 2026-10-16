using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using M2.Ledger;

namespace M2.Tests;

// Item 13: the Kit's rule that a Module names no transport type (ModuleRules.TransportTypesUsedBy: any type from an
// assembly named Wolverine* or JasperFx*), read from the assembly's type references, extended to Marten and Weasel. Marten
// 9's source generator writes an evolver into the Stream Module's own assembly, which names four JasperFx.Events types;
// they are the generator's, so the extended rule allows exactly those.
public sealed class StructureTests
{
    private static readonly HashSet<string> GeneratedEvolver =
    [
        "JasperFx.Events.Aggregation.GeneratedEvolverAttribute",
        "JasperFx.Events.Aggregation.IGeneratedSyncEvolver`2",
        "JasperFx.Events.Aggregation.JasperFxSourceGeneratorAppliedAttribute",
        "JasperFx.Events.IEvent",
    ];

    [Fact]
    public void Feature_code_of_a_stream_module_names_no_marten_weasel_wolverine_or_jasperfx_type_but_the_generated_evolvers()
    {
        var ledger = typeof(LedgerModule).Assembly;
        var offending = Assembly.Load("M2.Offending");

        // The Kit's rule as written rejects Ledger for the generator's output, and the offending assembly only for the
        // marker the generator puts on every assembly it runs over: it does not see Marten's session at all.
        Assert.Contains("JasperFx.Events.Aggregation.IGeneratedSyncEvolver`2", TypesFrom(ledger, KitTransport));
        Assert.Equal(["JasperFx.Events.Aggregation.JasperFxSourceGeneratorAppliedAttribute"], TypesFrom(offending, KitTransport));
        Assert.Empty(TypesFrom(ledger, StoreOrTransport).Except(GeneratedEvolver));
        Assert.Equal(["Marten.IDocumentOperations", "Marten.IDocumentSession"], TypesFrom(offending, StoreOrTransport).Except(GeneratedEvolver).Order());
    }

    private static bool KitTransport(string assembly) =>
        assembly.StartsWith("Wolverine", StringComparison.Ordinal) || assembly.StartsWith("JasperFx", StringComparison.Ordinal);

    private static bool StoreOrTransport(string assembly) =>
        KitTransport(assembly) || assembly.StartsWith("Marten", StringComparison.Ordinal) || assembly.StartsWith("Weasel", StringComparison.Ordinal);

    // The type references of the assembly that resolve to an assembly the predicate names, as TransportTypesUsedBy reads them.
    private static IEnumerable<string> TypesFrom(Assembly assembly, Func<string, bool> named)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        List<string> found = [];
        foreach (var handle in metadata.TypeReferences)
        {
            var type = metadata.GetTypeReference(handle);
            var scope = type.ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
            {
                scope = metadata.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            }

            if (scope.Kind == HandleKind.AssemblyReference && named(metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)))
            {
                found.Add($"{metadata.GetString(type.Namespace)}.{metadata.GetString(type.Name)}");
            }
        }

        return found;
    }
}
