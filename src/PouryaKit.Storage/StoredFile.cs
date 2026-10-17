namespace PouryaKit.Storage;

/// <summary>A stored file as downloaded: its content, read once from the store, and its content type.</summary>
public sealed class StoredFile(Stream content, string contentType) : IDisposable, IAsyncDisposable
{
    /// <summary>The file's bytes, streamed from the store; disposing the file closes it.</summary>
    public Stream Content { get; } = content;

    /// <summary>The content type the file was stored with, such as <c>application/pdf</c>.</summary>
    public string ContentType { get; } = contentType;

    /// <inheritdoc />
    public void Dispose() => Content.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
