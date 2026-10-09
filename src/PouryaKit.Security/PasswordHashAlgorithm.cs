namespace PouryaKit.Security;

/// <summary>The HMAC that PBKDF2 iterates.</summary>
public enum PasswordHashAlgorithm
{
    /// <summary>PBKDF2-HMAC-SHA256, encoded as <c>sha256</c>: at least 600,000 iterations and a 32-byte key.</summary>
    Sha256,

    /// <summary>PBKDF2-HMAC-SHA512, encoded as <c>sha512</c>: at least 220,000 iterations and a 32- to 64-byte key.</summary>
    Sha512,
}
