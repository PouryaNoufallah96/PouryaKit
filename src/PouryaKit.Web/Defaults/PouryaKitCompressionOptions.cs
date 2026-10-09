namespace PouryaKit.Web;

/// <summary>
/// Whether the Host compresses its responses, with ASP.NET Core's response compression (Brotli or gzip, as the caller's
/// <c>Accept-Encoding</c> asks, for its default content types, JSON included). Off by default.
/// </summary>
public sealed class PouryaKitCompressionOptions
{
    /// <summary>
    /// Compresses responses over HTTPS as well as HTTP. A response that reflects what the caller sent beside a secret
    /// of another party, such as an anti-forgery token, can leak that secret through its compressed length (BREACH):
    /// enable it for an API whose responses do not mix the two.
    /// </summary>
    public bool Enabled { get; set; }
}
