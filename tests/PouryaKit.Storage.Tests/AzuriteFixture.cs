using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PouryaKit.Storage.AzureBlob;
using Testcontainers.Azurite;

namespace PouryaKit.Storage.Tests;

// One Azurite container with the files container created, and one started Host whose file storage is that container,
// bound from configuration with the emulator's connection string as a Host binds it.
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string Container = "files";

    // Azurite's one account and its published key, which every emulator accepts.
    public const string AccountName = "devstoreaccount1";
    public const string AccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly AzuriteContainer _store = new AzuriteBuilder(
        "mcr.microsoft.com/azure-storage/azurite:3.37.0"
    )
        .WithInMemoryPersistence()
        .WithCommand("--skipApiVersionCheck")
        .Build();

    private IHost? _host;

    public IFileStorage Storage => _host!.Services.GetRequiredService<IFileStorage>();

    // The Blob service endpoint with the account in its path, as Azurite serves it.
    public string BlobEndpoint => _store.GetBlobEndpoint();

    public async ValueTask InitializeAsync()
    {
        await _store.StartAsync();
        await new BlobServiceClient(_store.GetConnectionString()).CreateBlobContainerAsync(
            Container
        );
        _host = ComposeHost(
            new()
            {
                ["PouryaKit:Storage:AzureBlob:Container"] = Container,
                ["PouryaKit:Storage:AzureBlob:ConnectionString"] = _store.GetConnectionString(),
            }
        );
        await _host.StartAsync();
    }

    public static IHost ComposeHost(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.Configure<PouryaKitAzureBlobStorageOptions>(
            builder.Configuration.GetSection("PouryaKit:Storage:AzureBlob")
        );
        builder.Services.AddPouryaKitAzureBlobStorage();
        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        _host?.Dispose();
        await _store.DisposeAsync();
    }
}
