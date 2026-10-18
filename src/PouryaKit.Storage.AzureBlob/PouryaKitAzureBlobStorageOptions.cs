namespace PouryaKit.Storage.AzureBlob;

/// <summary>
/// The Azure Blob Storage container the Host stores its files in and how to reach it, bound from configuration by the
/// Host, for example <c>Configure&lt;PouryaKitAzureBlobStorageOptions&gt;(configuration.GetSection("PouryaKit:Storage:AzureBlob"))</c>.
/// The account is reached either through <see cref="ConnectionString"/> or through <see cref="ServiceUrl"/> with
/// <see cref="AccountName"/> and <see cref="AccountKey"/>; the account key signs every presigned link.
/// </summary>
public sealed class PouryaKitAzureBlobStorageOptions
{
    /// <summary>The container every file is stored in; it must exist, since the Kit creates none.</summary>
    public string Container { get; set; } = "";

    /// <summary>
    /// The storage account's connection string with its account key, such as <c>DefaultEndpointsProtocol=https;AccountName=acme;AccountKey=…;EndpointSuffix=core.windows.net</c>;
    /// null, the default, to use <see cref="ServiceUrl"/> instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The account's Blob service endpoint, such as <c>https://acme.blob.core.windows.net</c>, set with
    /// <see cref="AccountName"/> and <see cref="AccountKey"/>; null, the default, to use <see cref="ConnectionString"/> instead.
    /// </summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>The storage account's name, set with <see cref="ServiceUrl"/>.</summary>
    public string? AccountName { get; set; }

    /// <summary>The storage account's base64 access key, set with <see cref="ServiceUrl"/>.</summary>
    public string? AccountKey { get; set; }
}
