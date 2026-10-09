namespace PouryaKit.Security;

/// <summary>What <see cref="IPasswordHasher.Verify"/> found.</summary>
public enum PasswordVerification
{
    /// <summary>The password does not match, or the stored hash is missing, malformed or unsupported.</summary>
    Failed,

    /// <summary>The password matches a hash made with the configured algorithm and at least its other parameters.</summary>
    Succeeded,

    /// <summary>
    /// The password matches, but the hash was made with another algorithm, fewer iterations or a shorter salt or key
    /// than configured: store <see cref="IPasswordHasher.Hash"/> of the password in its place.
    /// </summary>
    SucceededNeedsUpgrade,
}
