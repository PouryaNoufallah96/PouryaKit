using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Storage.S3;

namespace PouryaKit.Storage.Tests;

// One RustFS container and one started Host whose file storage is a bucket there, bound from configuration as a Host binds it.
public sealed class S3StoreFixture : IAsyncLifetime
{
    private const string Bucket = "files";
    private const string AccessKey = "kit-access";
    private const string SecretKey = "kit-secret-key";
    private const ushort S3Port = 9000;

    private readonly IContainer _store = new ContainerBuilder("rustfs/rustfs:1.0.2")
        .WithEnvironment("RUSTFS_ACCESS_KEY", AccessKey)
        .WithEnvironment("RUSTFS_SECRET_KEY", SecretKey)
        .WithPortBinding(S3Port, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(S3Port).ForPath("/health/ready")))
        .Build();

    private IHost? _host;

    public IFileStorage Storage => _host!.Services.GetRequiredService<IFileStorage>();

    public async ValueTask InitializeAsync()
    {
        await _store.StartAsync();
        var serviceUrl = $"http://{_store.Hostname}:{_store.GetMappedPublicPort(S3Port)}";
        using (var client = new AmazonS3Client(
                   new BasicAWSCredentials(AccessKey, SecretKey),
                   new AmazonS3Config { ServiceURL = serviceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }
               ))
        {
            await client.PutBucketAsync(Bucket);
        }

        _host = ComposeHost(
            new()
            {
                ["PouryaKit:Storage:S3:Bucket"] = Bucket,
                ["PouryaKit:Storage:S3:Region"] = "us-east-1",
                ["PouryaKit:Storage:S3:ServiceUrl"] = serviceUrl,
                ["PouryaKit:Storage:S3:ForcePathStyle"] = "true",
                ["PouryaKit:Storage:S3:AccessKey"] = AccessKey,
                ["PouryaKit:Storage:S3:SecretKey"] = SecretKey,
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
        await _store.DisposeAsync();
    }
}
