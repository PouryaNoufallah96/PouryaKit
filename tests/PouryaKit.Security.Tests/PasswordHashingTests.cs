using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace PouryaKit.Security.Tests;

public sealed class PasswordHashingTests
{
    private const string Password = "correct horse battery staple";

    // A hash of Password made outside the Kit, with the platform's PBKDF2 and the documented format.
    private static readonly byte[] KnownSalt = [.. Enumerable.Range(1, 16).Select(value => (byte)value)];
    private static readonly string Known = Encode("sha256", 600_000, KnownSalt, Derive(KnownSalt, 600_000, HashAlgorithmName.SHA256, 32));

    [Fact]
    public void The_default_hash_is_pbkdf2_hmac_sha256_with_600000_iterations_a_16_byte_salt_and_a_32_byte_key()
    {
        var encoded = Hasher().Hash(Password);

        var fields = encoded.Split('$');
        Assert.Equal(["pbkdf2", "1", "sha256", "600000"], fields[..4]);
        var salt = Convert.FromBase64String(fields[4]);
        Assert.Equal(16, salt.Length);
        Assert.Equal(Derive(salt, 600_000, HashAlgorithmName.SHA256, 32), Convert.FromBase64String(fields[5]));
        Assert.Equal(PasswordVerification.Succeeded, Hasher().Verify(Password, encoded));
    }

    [Fact]
    public void A_hash_made_outside_the_kit_in_the_documented_format_verifies()
    {
        Assert.Equal(PasswordVerification.Succeeded, Hasher().Verify(Password, Known));
    }

    [Fact]
    public void A_wrong_password_fails()
    {
        var hasher = Hasher();

        Assert.Equal(PasswordVerification.Failed, hasher.Verify(Password.ToUpperInvariant(), Known));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify(Password + " ", Known));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify("", Known));
    }

    [Fact]
    public void Two_hashes_of_one_password_have_different_salts_and_both_verify()
    {
        var hasher = Hasher();

        var first = hasher.Hash(Password);
        var second = hasher.Hash(Password);

        Assert.NotEqual(first.Split('$')[4], second.Split('$')[4]);
        Assert.NotEqual(first.Split('$')[5], second.Split('$')[5]);
        Assert.Equal(PasswordVerification.Succeeded, hasher.Verify(Password, first));
        Assert.Equal(PasswordVerification.Succeeded, hasher.Verify(Password, second));
    }

    [Theory]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 16, 32, PasswordHashAlgorithm.Sha256, 600_000, 16, 32, PasswordVerification.Succeeded)]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 16, 32, PasswordHashAlgorithm.Sha256, 700_000, 16, 32, PasswordVerification.SucceededNeedsUpgrade)]
    [InlineData(PasswordHashAlgorithm.Sha256, 700_000, 16, 32, PasswordHashAlgorithm.Sha256, 600_000, 16, 32, PasswordVerification.Succeeded)]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 16, 32, PasswordHashAlgorithm.Sha256, 600_000, 32, 32, PasswordVerification.SucceededNeedsUpgrade)]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 16, 32, PasswordHashAlgorithm.Sha512, 600_000, 16, 32, PasswordVerification.SucceededNeedsUpgrade)]
    [InlineData(PasswordHashAlgorithm.Sha512, 220_000, 16, 32, PasswordHashAlgorithm.Sha512, 220_000, 16, 64, PasswordVerification.SucceededNeedsUpgrade)]
    public void A_hash_verifies_with_the_parameters_it_carries_and_needs_an_upgrade_only_when_weaker_than_configured(
        PasswordHashAlgorithm madeWith,
        int madeIterations,
        int madeSaltSize,
        int madeKeySize,
        PasswordHashAlgorithm configured,
        int configuredIterations,
        int configuredSaltSize,
        int configuredKeySize,
        PasswordVerification expected
    )
    {
        var stored = Hasher(options =>
            {
                options.Algorithm = madeWith;
                options.Iterations = madeIterations;
                options.SaltSize = madeSaltSize;
                options.KeySize = madeKeySize;
            })
            .Hash(Password);
        var current = Hasher(options =>
        {
            options.Algorithm = configured;
            options.Iterations = configuredIterations;
            options.SaltSize = configuredSaltSize;
            options.KeySize = configuredKeySize;
        });

        Assert.Equal(expected, current.Verify(Password, stored));
        Assert.Equal(PasswordVerification.Failed, current.Verify("not the password", stored));
    }

    [Fact]
    public void A_hash_with_fewer_iterations_than_options_now_allow_still_verifies_and_needs_an_upgrade()
    {
        var legacy = Encode("sha256", 310_000, KnownSalt, Derive(KnownSalt, 310_000, HashAlgorithmName.SHA256, 32));

        Assert.Equal(PasswordVerification.SucceededNeedsUpgrade, Hasher().Verify(Password, legacy));
    }

    public static TheoryData<string, string?> Malformed()
    {
        var fields = Known.Split('$');
        string With(int index, string value) => string.Join('$', fields.Select((field, at) => at == index ? value : field));
        var key = Convert.FromBase64String(fields[5]);
        return new()
        {
            { "empty", "" },
            { "missing", null },
            { "another scheme", With(0, "bcrypt") },
            { "prefix in upper case", With(0, "PBKDF2") },
            { "five fields", string.Join('$', fields[..5]) },
            { "seven fields", Known + "$" },
            { "unsupported version", With(1, "2") },
            { "version with a leading zero", With(1, "01") },
            { "SHA-1, legacy only", With(2, "sha1") },
            { "SHA-384, not supported", With(2, "sha384") },
            { "algorithm in upper case", With(2, "SHA256") },
            { "zero iterations", With(3, "0") },
            { "negative iterations", With(3, "-600000") },
            { "iterations with a sign", With(3, "+600000") },
            { "iterations with a leading zero", With(3, "0600000") },
            { "iterations with a space", With(3, " 600000") },
            { "iterations with a separator", With(3, "600,000") },
            { "iterations with a trailing NUL", With(3, "600000\0") },
            { "iterations with a leading NUL", With(3, "\0600000") },
            { "iterations with a trailing space", With(3, "600000 ") },
            { "iterations in Arabic-Indic digits", With(3, "٦٠٠٠٠٠") },
            { "iterations in full-width digits", With(3, "６００００００") },
            { "iterations beyond int", With(3, "2147483648") },
            { "iterations above the ceiling", With(3, "10000001") },
            { "salt not base64", With(4, "!!!!!!!!!!!!!!!!!!!!!!!!") },
            { "salt with white space", With(4, fields[4][..8] + " " + fields[4][8..]) },
            { "salt without padding", With(4, fields[4].TrimEnd('=')) },
            { "salt of 15 bytes", With(4, Convert.ToBase64String(KnownSalt[..15])) },
            { "salt of 65 bytes", With(4, Convert.ToBase64String(new byte[65])) },
            { "key not base64", With(5, "%%%%") },
            { "empty key", With(5, "") },
            { "key truncated to 31 bytes", With(5, Convert.ToBase64String(key[..31])) },
            { "key longer than SHA-256's output", With(5, Convert.ToBase64String([.. key, 0])) },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_or_unsupported_encoding_fails_for_the_right_password_without_throwing(string because, string? encoded)
    {
        Assert.True(Hasher().Verify(Password, encoded) == PasswordVerification.Failed, because);
    }

    [Fact]
    public void A_missing_password_or_one_with_no_utf8_form_fails_verification_without_throwing()
    {
        Assert.Equal(PasswordVerification.Failed, Hasher().Verify(null!, Known));
        Assert.Equal(PasswordVerification.Failed, Hasher().Verify("\ud800", Known));
    }

    [Fact]
    public void Verification_compares_derived_keys_in_constant_time()
    {
        var calls = CallsIn(Hasher().GetType()).ToList();

        Assert.Contains(calls, call => call.DeclaringType == typeof(CryptographicOperations) && call.Name == "FixedTimeEquals");
        Assert.DoesNotContain(calls, call => call.Name == "SequenceEqual");
    }

    private static IPasswordHasher Hasher(Action<PouryaKitPasswordHashingOptions>? configure = null) =>
        new ServiceCollection().AddPouryaKitPasswordHashing(configure).BuildServiceProvider().GetRequiredService<IPasswordHasher>();

    private static byte[] Derive(byte[] salt, int iterations, HashAlgorithmName algorithm, int keySize) =>
        Rfc2898DeriveBytes.Pbkdf2(Password, salt, iterations, algorithm, keySize);

    private static string Encode(string algorithm, int iterations, byte[] salt, byte[] key) =>
        $"pbkdf2$1${algorithm}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";

    // Every method a call or callvirt in the type's own method bodies names. Each byte offset is tried, so a real call
    // is never missed; a token read off a misaligned offset fails to resolve and is skipped.
    private static IEnumerable<MethodBase> CallsIn(Type type)
    {
        const BindingFlags all =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
            for (var at = 0; at + 4 < il.Length; at++)
            {
                if (il[at] is not (0x28 or 0x6f))
                {
                    continue;
                }

                MethodBase? callee = null;
                try
                {
                    callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, at + 1));
                }
                catch (ArgumentException) { }

                if (callee is not null)
                {
                    yield return callee;
                }
            }
        }
    }
}
