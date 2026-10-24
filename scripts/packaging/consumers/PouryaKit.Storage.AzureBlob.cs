using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Storage;
using PouryaKit.Storage.AzureBlob;

// What a Host calls on the packed provider: its container's options, then the Host's one file storage provider.
// Compiled, never run.
var services = new ServiceCollection().AddPouryaKitAzureBlobStorage(blob =>
{
    blob.Container = "files";
    blob.ConnectionString = "UseDevelopmentStorage=true";
});
Console.WriteLine(services.Count(descriptor => descriptor.ServiceType == typeof(IFileStorage)));
