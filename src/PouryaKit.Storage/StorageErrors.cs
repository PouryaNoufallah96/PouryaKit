using PouryaKit.Domain;

namespace PouryaKit.Storage;

/// <summary>The Errors a file storage provider fails a call with.</summary>
public static class StorageErrors
{
    /// <summary>No file is stored under the key the call names.</summary>
    public static readonly Error FileNotFound = Error.NotFound("Storage.FileNotFound", "No file is stored under this key.");

    /// <summary>
    /// A presigned link's lifetime is zero or less, or longer than the provider signs a link for: S3 signs one for at most
    /// seven days. No link was signed.
    /// </summary>
    public static readonly Error LinkLifetimeOutOfRange = Error.Validation(
        "Storage.LinkLifetimeOutOfRange",
        "The link's lifetime is zero or less, or longer than seven days, the longest S3 signs a link for."
    );
}
