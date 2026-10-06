namespace PouryaKit.Security;

/// <summary>
/// Hashes a password for storage and checks a password against a stored hash, with PBKDF2 from the platform's
/// cryptography. Registered by <c>AddPouryaKitPasswordHashing</c>; the Kit stores nothing and has no identity system.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// A new encoded hash of <paramref name="password"/> under the configured parameters, with a fresh random salt, in
    /// the form <c>pbkdf2$1$&lt;algorithm&gt;$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 key&gt;</c>.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Whether <paramref name="password"/> is the one <paramref name="encodedHash"/> was made from, derived with the
    /// parameters the encoding carries, and whether that hash is weaker than the configured parameters. A missing
    /// password, or a missing, malformed or unsupported encoding, is <see cref="PasswordVerification.Failed"/>; it
    /// never throws.
    /// </summary>
    PasswordVerification Verify(string password, string? encodedHash);
}
