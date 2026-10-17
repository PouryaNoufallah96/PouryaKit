using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.ReverseProxy;
using Yarp.ReverseProxy.Configuration;

// What a proxy Host calls on the packed Reverse proxy: its routes and clusters from configuration, mapped with YARP's
// own endpoints. Compiled, never run.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDiscovery();
builder.AddPouryaKitReverseProxy();
var app = builder.Build();
app.MapReverseProxy();
Console.WriteLine(app.Services.GetServices<IProxyConfigFilter>().Count());
