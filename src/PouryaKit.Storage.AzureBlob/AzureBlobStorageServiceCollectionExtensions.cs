using Azure.Storage;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PouryaKit.Storage.AzureBlob;

/// <summary>Composes the file storage Capability on a Host's services with an Azure Blob Storage container as its provider.</summary>
public static class AzureBlobStorageServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Makes an Azure Blob Storage container, or one of a local emulator such as Azurite, the Host's one
        /// <see cref="IFileStorage"/>, a singleton built from <see cref="PouryaKitAzureBlobStorageOptions"/> when first
        /// resolved. The Host binds the options from configuration; <paramref name="configure"/> sets them in code
        /// instead. The Host fails when it starts, naming each problem, when the options name no container, set both or
        /// neither of the connection string and the service URL, a connection string that does not parse or carries no
        /// account key, a service URL that is not an absolute HTTP or HTTPS address, an account name or key without the
        /// service URL or the reverse, or an account key that is not base64. A Host composes exactly one
        /// <see cref="IFileStorage"/>: its build fails, through the Kit's composition checks, naming each registration's
        /// type when it registers another, before or after this call, a second call included, and naming the contract
        /// when it removes this one.
        /// </summary>
        public IServiceCollection AddPouryaKitAzureBlobStorage(Action<PouryaKitAzureBlobStorageOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var options = services
                .AddOptions<PouryaKitAzureBlobStorageOptions>()
                .Validate(blob => !string.IsNullOrWhiteSpace(blob.Container), "PouryaKitAzureBlobStorageOptions.Container names no container to store files in.")
                .Validate(
                    blob => string.IsNullOrWhiteSpace(blob.ConnectionString) != blob.ServiceUrl is null,
                    "PouryaKitAzureBlobStorageOptions.ConnectionString or ServiceUrl is set, one and not both."
                )
                .Validate(
                    blob => string.IsNullOrWhiteSpace(blob.ConnectionString) || SignsLinks(blob.ConnectionString),
                    "PouryaKitAzureBlobStorageOptions.ConnectionString is not a storage connection string with an account key, which signs the presigned links."
                )
                .Validate(
                    blob => blob.ServiceUrl is null || (blob.ServiceUrl.IsAbsoluteUri && (blob.ServiceUrl.Scheme == Uri.UriSchemeHttp || blob.ServiceUrl.Scheme == Uri.UriSchemeHttps)),
                    "PouryaKitAzureBlobStorageOptions.ServiceUrl is not an absolute HTTP or HTTPS address."
                )
                .Validate(
                    blob => blob.ServiceUrl is null == string.IsNullOrWhiteSpace(blob.AccountName) && blob.ServiceUrl is null == string.IsNullOrWhiteSpace(blob.AccountKey),
                    "PouryaKitAzureBlobStorageOptions.AccountName and AccountKey are set with ServiceUrl, and only with it."
                )
                .Validate(
                    blob => string.IsNullOrWhiteSpace(blob.AccountKey) || IsAccountKey(blob.AccountKey),
                    "PouryaKitAzureBlobStorageOptions.AccountKey is not a base64 account key."
                )
                .ValidateOnStart();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            return services.AddPouryaKitFileStorage(Create);
        }
    }

    // Parsing builds no connection: the client only reads the string and reports whether it holds a key to sign with.
    private static bool SignsLinks(string connectionString)
    {
        try
        {
            return new BlobServiceClient(connectionString).CanGenerateAccountSasUri;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // The credential the provider signs with decodes the key as it is built, as the client parses its connection string.
    private static bool IsAccountKey(string accountKey)
    {
        try
        {
            _ = new StorageSharedKeyCredential("account", accountKey);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // A named method, which the one-provider check names.
    private static AzureBlobFileStorage Create(IServiceProvider provider) => new(provider.GetRequiredService<IOptions<PouryaKitAzureBlobStorageOptions>>().Value);
}
