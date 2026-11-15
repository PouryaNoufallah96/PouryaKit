using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;

System.Type[] types = [typeof(PouryaKitOpenApiOptions)];

// The packed Web.OpenApi registered and mapped as a Host and a Module's Composition Root would: the Host's API
// versioning with a configured sunset, a Module's declared versions, its documents and the Scalar reference, and a
// document of the Host's own, whose AddOpenApi the OpenAPI source generator intercepts: it compiles only because the
// package lets Microsoft.AspNetCore.OpenApi's build targets, which enable those interceptors, flow to the Host.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPouryaKitWeb();
builder.Services.AddPouryaKitOpenApi(openApi =>
    openApi.Sunsets["1.0"] = new ApiVersionSunset
    {
        Date = new DateTimeOffset(2027, 6, 30, 0, 0, 0, TimeSpan.Zero),
    }
);
builder.Services.AddPouryaKitApiVersions("/notes", versions =>
    versions.HasDeprecatedApiVersion(new ApiVersion(1, 0)).HasApiVersion(new ApiVersion(2, 0))
);
builder.Services.AddOpenApi("host");
using var app = builder.Build();
app.MapPouryaKitOpenApi().AllowAnonymous();
Console.WriteLine(types.Length);
