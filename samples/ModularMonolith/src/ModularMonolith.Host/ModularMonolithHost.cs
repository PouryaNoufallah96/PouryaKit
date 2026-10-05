using Catalog;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using PouryaKit.Modularity;
using PouryaKit.Web;

namespace ModularMonolith.Host;

/// <summary>The Host's composition, shared by <c>Program</c> and the tests that host the application.</summary>
public static class ModularMonolithHost
{
    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // The Host authenticates its callers itself. JwtBearer reads its authority, audiences and signing keys from
        // the configuration section Authentication:Schemes:Bearer; `dotnet user-jwts` fills it for development.
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddAuthorization();
        builder.Services.AddPouryaKitWeb();
        builder.AddPouryaKitModules(modules => modules.Add<CatalogModule>());
        return builder;
    }

    /// <summary>Builds the application <paramref name="builder"/> composed, with its request pipeline.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        // First, so an exception in routing or authentication still answers a problem response.
        app.UseExceptionHandler();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapPouryaKitModuleEndpoints();
        return app;
    }
}
