using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.AI;
using PouryaKit.AI.Postgres;

// What a Host and a Module call on the packed AI.Postgres: the Host's embedding generator, a Module's vector store and
// the migration that creates its collection, and a store and a search from Feature code. Compiled, never run.
var services = new ServiceCollection().AddPouryaKitEmbeddings().AddPouryaKitVectorStore();
Action<MigrationBuilder> migrate = migrationBuilder =>
    migrationBuilder.CreateVectorCollection("products", dimensions: 384);
Func<IVectorStore, Task<IReadOnlyList<VectorMatch>>> search = async vectors =>
{
    await vectors.StoreAsync(
        "products",
        "lamp",
        "A brass lamp for a reading desk.",
        CancellationToken.None
    );
    return await vectors.SearchAsync(
        "products",
        "a light to read by",
        top: 3,
        CancellationToken.None
    );
};
Console.WriteLine(
    $"{services.Count(descriptor => descriptor.ServiceType == typeof(IVectorStore))} {migrate.Method.Name} {search.Method.Name} {VectorCollectionTable.Schema}"
);
