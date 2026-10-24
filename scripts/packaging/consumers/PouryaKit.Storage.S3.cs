using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Storage;
using PouryaKit.Storage.S3;

// What a Host calls on the packed provider: its bucket's options, then the Host's one file storage provider. Compiled,
// never run.
var services = new ServiceCollection().AddPouryaKitS3Storage(s3 =>
{
    s3.Bucket = "files";
    s3.Region = "eu-central-1";
});
Console.WriteLine(services.Count(descriptor => descriptor.ServiceType == typeof(IFileStorage)));
