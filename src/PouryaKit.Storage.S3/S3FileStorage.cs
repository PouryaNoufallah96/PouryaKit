using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using PouryaKit.Domain;

namespace PouryaKit.Storage.S3;

internal sealed class S3FileStorage : IFileStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;
    private readonly Protocol _linkProtocol;

    public S3FileStorage(PouryaKitS3StorageOptions options)
    {
        var config = new AmazonS3Config { ForcePathStyle = options.ForcePathStyle };
        if (options.ServiceUrl is null)
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }
        else
        {
            config.ServiceURL = options.ServiceUrl.AbsoluteUri;
            config.AuthenticationRegion = options.Region;
        }

        _client = string.IsNullOrWhiteSpace(options.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        _bucket = options.Bucket;
        // The SDK signs a link for HTTPS whatever the endpoint, so an HTTP-only store such as a local MinIO needs HTTP.
        _linkProtocol = options.ServiceUrl?.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;
    }

    public async Task<Result> UploadAsync(string key, Stream content, string contentType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        await _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = content,
                ContentType = contentType,
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
            },
            cancellationToken
        );
        return Result.Success();
    }

    public async Task<Result<StoredFile>> DownloadAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
            return new StoredFile(response.ResponseStream, response.Headers.ContentType);
        }
        catch (NoSuchKeyException)
        {
            return StorageErrors.FileNotFound;
        }
    }

    public async Task<Result> DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<Uri>> GetPresignedUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (lifetime <= TimeSpan.Zero || lifetime > MaxLinkLifetime)
        {
            return StorageErrors.LinkLifetimeOutOfRange;
        }

        // The SDK's call takes no token and completes at once with a configured key pair, so a token cancelled already is
        // checked first; while the SDK resolves credentials from its default chain, a cancelled caller stops waiting
        // and the SDK may still sign the link it no longer returns.
        cancellationToken.ThrowIfCancellationRequested();
        // The SDK signs on the system clock and counts the link's lifetime from it, so the expiry is read from that
        // clock too.
        var link = await _client.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = _bucket,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(lifetime),
                Protocol = _linkProtocol,
            }
        ).WaitAsync(cancellationToken);
        return new Uri(link);
    }

    public void Dispose() => _client.Dispose();

    // The longest a link signed with Signature Version 4 is valid for, and so S3's maximum.
    private static readonly TimeSpan MaxLinkLifetime = TimeSpan.FromDays(7);
}
