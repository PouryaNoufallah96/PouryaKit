using PouryaKit.Domain;

namespace PouryaKit.Storage;

/// <summary>The Errors a file storage provider fails a call with.</summary>
public static class StorageErrors
{
    /// <summary>No file is stored under the key the call names.</summary>
    public static readonly Error FileNotFound = Error.NotFound("Storage.FileNotFound", "No file is stored under this key.");
}
