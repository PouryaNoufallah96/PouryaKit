using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PouryaKit.Security;

// PBKDF2 from System.Security.Cryptography, in the encoded format pbkdf2$1$<algorithm>$<iterations>$<salt>$<key>.
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Prefix = "pbkdf2";
    private const string Version = "1";
    private const int MaxIterations = 10_000_000;
    private const int MinSaltSize = 16;
    private const int MaxSaltSize = 64;

    // A stored key may not be shorter than this: PBKDF2's shorter output is a prefix of its longer one, so a bound is
    // what keeps a cut-down key from verifying.
    private const int MinKeySize = 32;

    private readonly Pbkdf2Algorithm algorithm;
    private readonly int iterations;
    private readonly int saltSize;
    private readonly int keySize;

    private Pbkdf2PasswordHasher(Pbkdf2Algorithm algorithm, int iterations, int saltSize, int keySize)
    {
        this.algorithm = algorithm;
        this.iterations = iterations;
        this.saltSize = saltSize;
        this.keySize = keySize;
    }

    public static Pbkdf2PasswordHasher Create(PasswordHashingOptions options)
    {
        var algorithm = Pbkdf2Algorithm.Of(options.Algorithm);
        List<string> problems = [];
        if (algorithm is null)
        {
            problems.Add($"Algorithm {options.Algorithm} is not one of {string.Join(", ", Enum.GetNames<PasswordHashAlgorithm>())}.");
        }
        else
        {
            if (options.Iterations < algorithm.MinIterations || options.Iterations > MaxIterations)
            {
                problems.Add(
                    $"Iterations {options.Iterations} is outside {algorithm.MinIterations:N0} to {MaxIterations:N0} for {options.Algorithm}."
                );
            }

            if (options.KeySize < MinKeySize || options.KeySize > algorithm.OutputSize)
            {
                problems.Add($"KeySize {options.KeySize} is outside {MinKeySize} to {algorithm.OutputSize} bytes for {options.Algorithm}.");
            }
        }

        if (options.SaltSize < MinSaltSize || options.SaltSize > MaxSaltSize)
        {
            problems.Add($"SaltSize {options.SaltSize} is outside {MinSaltSize} to {MaxSaltSize} bytes.");
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"The password hashing options are invalid:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}"
            );
        }

        return new(algorithm!, options.Iterations, options.SaltSize, options.KeySize);
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(saltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, algorithm.HashName, keySize);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}${Version}${algorithm.Name}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}"
        );
    }

    public PasswordVerification Verify(string password, string? encodedHash)
    {
        if (password is null || !TryDecode(encodedHash, out var stored))
        {
            return PasswordVerification.Failed;
        }

        byte[] derived;
        try
        {
            derived = Rfc2898DeriveBytes.Pbkdf2(password, stored.Salt, stored.Iterations, stored.Algorithm.HashName, stored.Key.Length);
        }
        catch (EncoderFallbackException)
        {
            // A password with a lone surrogate has no UTF-8 form, so no stored hash can be of it.
            return PasswordVerification.Failed;
        }

        var matches = CryptographicOperations.FixedTimeEquals(derived, stored.Key);
        CryptographicOperations.ZeroMemory(derived);
        if (!matches)
        {
            return PasswordVerification.Failed;
        }

        return stored.Algorithm != algorithm
            || stored.Iterations < iterations
            || stored.Salt.Length < saltSize
            || stored.Key.Length < keySize
            ? PasswordVerification.SucceededNeedsUpgrade
            : PasswordVerification.Succeeded;
    }

    private static bool TryDecode(string? encodedHash, out StoredHash stored)
    {
        stored = default;
        if (
            encodedHash?.Split('$') is not [Prefix, Version, var name, var iterationsText, var saltText, var keyText]
            || Pbkdf2Algorithm.Named(name) is not { } algorithm
            || !TryParseIterations(iterationsText, out var iterations)
            || !TryDecodeBase64(saltText, MinSaltSize, MaxSaltSize, out var salt)
            || !TryDecodeBase64(keyText, MinKeySize, algorithm.OutputSize, out var key)
        )
        {
            return false;
        }

        stored = new(algorithm, iterations, salt, key);
        return true;
    }

    // Digits only, no sign, space or leading zero, from 1 to the ceiling, so a stored row cannot make Verify spin.
    private static bool TryParseIterations(string text, out int iterations) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
        && text[0] != '0'
        && iterations <= MaxIterations;

    // Canonical padded base64 only: the decoder skips white space, so the bytes must encode back to the same text.
    private static bool TryDecodeBase64(string text, int minLength, int maxLength, out byte[] bytes)
    {
        bytes = [];
        Span<byte> buffer = stackalloc byte[maxLength];
        if (
            !Convert.TryFromBase64String(text, buffer, out var written)
            || written < minLength
            || !string.Equals(Convert.ToBase64String(buffer[..written]), text, StringComparison.Ordinal)
        )
        {
            return false;
        }

        bytes = buffer[..written].ToArray();
        return true;
    }

    private readonly record struct StoredHash(Pbkdf2Algorithm Algorithm, int Iterations, byte[] Salt, byte[] Key);

    private sealed record Pbkdf2Algorithm(string Name, HashAlgorithmName HashName, int OutputSize, int MinIterations)
    {
        // Minimum iterations from the OWASP Password Storage Cheat Sheet's PBKDF2 section (checked 2026-10-07).
        private static readonly Pbkdf2Algorithm Sha256 = new("sha256", HashAlgorithmName.SHA256, 32, 600_000);
        private static readonly Pbkdf2Algorithm Sha512 = new("sha512", HashAlgorithmName.SHA512, 64, 220_000);

        public static Pbkdf2Algorithm? Of(PasswordHashAlgorithm algorithm) =>
            algorithm switch
            {
                PasswordHashAlgorithm.Sha256 => Sha256,
                PasswordHashAlgorithm.Sha512 => Sha512,
                _ => null,
            };

        public static Pbkdf2Algorithm? Named(string name) =>
            name switch
            {
                "sha256" => Sha256,
                "sha512" => Sha512,
                _ => null,
            };
    }
}
