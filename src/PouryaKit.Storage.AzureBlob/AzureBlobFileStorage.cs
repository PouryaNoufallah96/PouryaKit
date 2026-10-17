using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using PouryaKit.Domain;

namespace PouryaKit.Storage.AzureBlob;

internal sealed class AzureBlobFileStorage(PouryaKitAzureBlobStorageOptions options) : IFileStorage
{
    private readonly BlobContainerClient _container = (
        !string.IsNullOrWhiteSpace(options.ConnectionString)
            ? new BlobServiceClient(options.ConnectionString)
            : new BlobServiceClient(options.ServiceUrl, new StorageSharedKeyCredential(options.AccountName, options.AccountKey))
    ).GetBlobContainerClient(options.Container);

    public async Task<Result> UploadAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        // Without conditions the upload replaces any blob under the key.
        await _container
            .GetBlobClient(key)
            .UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<StoredFile>> DownloadAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var download = (await _container.GetBlobClient(key).DownloadStreamingAsync(cancellationToken: cancellationToken)).Value;
            return new StoredFile(download.Content, download.Details.ContentType);
        }
        catch (RequestFailedException failure) when (failure.ErrorCode == BlobErrorCode.BlobNotFound)
        {
            return StorageErrors.FileNotFound;
        }
    }

    public async Task<Result> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _container.GetBlobClient(key).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        return Result.Success();
    }

    public Task<Result<Uri>> GetPresignedUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLinkLifetime)
        {
            return Task.FromResult<Result<Uri>>(StorageErrors.LinkLifetimeOutOfRange(MaxLinkLifetime));
        }

        // The SDK signs locally with the account key and takes no token, so only a token cancelled already is observed.
        // The link is a service SAS that reads this one blob, expiring by the store's clock.
        return cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<Result<Uri>>(cancellationToken)
            : Task.FromResult<Result<Uri>>(_container.GetBlobClient(key).GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(lifetime)));
    }

    // Azure bounds a link signed with the account key by no limit of its own; the Kit signs one for at most the seven
    // days a user delegation SAS allows, the bound S3 has too, so a lifetime a Feature asks for is valid on either provider.
    private static readonly TimeSpan MaxLinkLifetime = TimeSpan.FromDays(7);
}
