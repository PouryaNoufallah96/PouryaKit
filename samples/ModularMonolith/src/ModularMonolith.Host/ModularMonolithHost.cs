using Catalog;
using Microsoft.Extensions.Hosting;
using PouryaKit.Modularity;

namespace ModularMonolith.Host;

/// <summary>The Host's composition, shared by <c>Program</c> and the tests that host the application.</summary>
public static class ModularMonolithHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>());
        return builder;
    }
}
