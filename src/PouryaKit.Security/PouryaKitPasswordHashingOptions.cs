namespace PouryaKit.Security;

/// <summary>
/// The parameters new hashes are made with, checked when the Host starts. The defaults and minimums follow the OWASP
/// Password Storage Cheat Sheet for PBKDF2 (checked 2026-10-07).
/// </summary>
public sealed class PouryaKitPasswordHashingOptions
{
    /// <summary>The HMAC PBKDF2 iterates; <see cref="PasswordHashAlgorithm.Sha256"/> by default.</summary>
    public PasswordHashAlgorithm Algorithm { get; set; } = PasswordHashAlgorithm.Sha256;

    /// <summary>
    /// The work factor: 600,000 by default; at least 600,000 for SHA-256 and 220,000 for SHA-512, at most 10,000,000.
    /// </summary>
    public int Iterations { get; set; } = 600_000;

    /// <summary>The random salt's length in bytes: 16 by default, from 16 to 64.</summary>
    public int SaltSize { get; set; } = 16;

    /// <summary>The derived key's length in bytes: 32 by default, from 32 to the algorithm's output (32 or 64).</summary>
    public int KeySize { get; set; } = 32;
}
