using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Storage.S3;
using Testcontainers.Minio;

namespace PouryaKit.Storage.Tests;

// One MinIO container and one started Host whose file storage is a bucket there, bound from configuration as a Host binds it.
public sealed class MinioFixture : IAsyncLifetime
{
    private const string Bucket = "files";
    private readonly MinioContainer _minio = new MinioBuilder("minio/minio:RELEASE.2025-04-22T22-12-26Z").Build();
    private IHost? _host;

    public IFileStorage Storage => _host!.Services.GetRequiredService<IFileStorage>();

    public async ValueTask InitializeAsync()
    {
        await _minio.StartAsync();
        using (var client = new AmazonS3Client(
                   new BasicAWSCredentials(_minio.GetAccessKey(), _minio.GetSecretKey()),
                   new AmazonS3Config { ServiceURL = _minio.GetConnectionString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" }
               ))
        {
            await client.PutBucketAsync(Bucket);
        }

        _host = ComposeHost(
            new()
            {
                ["PouryaKit:Storage:S3:Bucket"] = Bucket,
                ["PouryaKit:Storage:S3:Region"] = "us-east-1",
                ["PouryaKit:Storage:S3:ServiceUrl"] = _minio.GetConnectionString(),
                ["PouryaKit:Storage:S3:ForcePathStyle"] = "true",
                ["PouryaKit:Storage:S3:AccessKey"] = _minio.GetAccessKey(),
                ["PouryaKit:Storage:S3:SecretKey"] = _minio.GetSecretKey(),
            }
        );
        await _host.StartAsync();
    }

    public static IHost ComposeHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.Configure<PouryaKitS3StorageOptions>(builder.Configuration.GetSection("PouryaKit:Storage:S3"));
        builder.Services.AddPouryaKitS3Storage();
        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        await _minio.DisposeAsync();
    }
}
