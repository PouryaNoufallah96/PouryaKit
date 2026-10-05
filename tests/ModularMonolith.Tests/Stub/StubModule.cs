using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;

namespace ModularMonolith.Tests.Stub;

// A second Module, for tests only, with its own DbContext and schema beside Catalog's: with one Module in the Host, a
// Unit of Work resolved for the wrong Module would go unnoticed.
public sealed class StubModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<StubDbContext>(connectionName: "app", schema: "stub");
        module.AddPouryaKitCqrs(typeof(StubModule).Assembly);
        module.AddScoped<ReactionLog>();
    }
}
