using PouryaKit.Domain;

namespace PouryaKit.Storage;

/// <summary>
/// The Host's one file storage provider: stores, reads and deletes a file under a key the caller chooses, and grants a
/// time-limited link to read it. A file is stored before a command records its key; a command that then fails leaves
/// the object behind for the application's retention to remove, since no store joins the Unit of Work's commit.
/// A failure of the store itself, such as a refused credential or an unreachable endpoint, throws.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Stores <paramref name="content"/>, read from its current position to its end, as the file under
    /// <paramref name="key"/> with <paramref name="contentType"/>, replacing any file stored there.
    /// </summary>
    Task<Result> UploadAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// The file under <paramref name="key"/>, whose content the caller reads and disposes; <see cref="StorageErrors.FileNotFound"/>
    /// when none is stored there.
    /// </summary>
    Task<Result<StoredFile>> DownloadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Deletes the file under <paramref name="key"/>; a success too when none is stored there.</summary>
    Task<Result> DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// A link that reads the file under <paramref name="key"/> without credentials for <paramref name="lifetime"/>. The
    /// store is not asked whether the file exists: a link to a missing file answers not found when it is followed.
    /// <see cref="StorageErrors.LinkLifetimeOutOfRange"/> when <paramref name="lifetime"/> is zero or less, or longer than
    /// the provider signs a link for.
    /// </summary>
    Task<Result<Uri>> GetPresignedUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken);
}
