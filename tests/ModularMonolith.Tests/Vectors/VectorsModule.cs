using PouryaKit.AI.Postgres;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;

namespace ModularMonolith.Tests.Vectors;

/// <summary>A tenant-scoped Module that stores passages by their embedding and searches them.</summary>
public sealed class VectorsModule : IModuleDefinition
{
    /// <summary>The vector collection the Module's migration creates.</summary>
    public const string Passages = "passages";

    /// <summary>The dimensions of the embeddings the tests' model returns.</summary>
    public const int Dimensions = 8;

    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<VectorsDbContext>(connectionName: "app", schema: "vectors");
        module.AddPouryaKitCqrs();
        module.AddPouryaKitTenancy();
        module.AddPouryaKitVectorStore();
    }
}
