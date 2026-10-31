using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ModularMonolith.Tests.Fixtures;

/// <summary>
/// An identity provider of the tests' own: it signs tokens with a key generated for this test run, and every Host the
/// tests compose trusts it through the sample Host's <c>Authentication:Schemes:Bearer</c> section, as
/// <c>dotnet user-jwts</c> fills it.
/// </summary>
internal static class TestIssuer
{
    public const string Issuer = "https://issuer.modular-monolith.test";

    public const string Audience = "modular-monolith";

    private static readonly SymmetricSecurityKey Key = new(RandomNumberGenerator.GetBytes(32));

    /// <summary>The key this issuer signs with, for a Host that resolves its signing keys itself.</summary>
    public static SecurityKey SigningKey => Key;

    /// <summary>The command-line configuration that makes a Host trust this issuer.</summary>
    public static string[] HostArguments { get; } =
    [
        $"--Authentication:Schemes:Bearer:ValidIssuer={Issuer}",
        $"--Authentication:Schemes:Bearer:ValidAudiences:0={Audience}",
        $"--Authentication:Schemes:Bearer:SigningKeys:0:Issuer={Issuer}",
        $"--Authentication:Schemes:Bearer:SigningKeys:0:Value={Convert.ToBase64String(Key.Key)}",
    ];

    /// <summary>
    /// <see cref="HostArguments"/> in the environment's spelling, for a Host run as its own process: JasperFx reads the
    /// Host's arguments as its own.
    /// </summary>
    public static IReadOnlyDictionary<string, string> HostEnvironment { get; } =
        HostArguments
            .Select(argument => argument.TrimStart('-').Split('=', 2))
            .ToDictionary(
                setting => setting[0].Replace(":", "__", StringComparison.Ordinal),
                setting => setting[1]
            );

    /// <summary>
    /// A token for the caller <paramref name="claims"/> describe, valid now for five minutes unless
    /// <paramref name="change"/> says otherwise.
    /// </summary>
    public static string Token(
        Dictionary<string, object> claims,
        Action<SecurityTokenDescriptor>? change = null
    )
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = Signing(Key),
        };
        change?.Invoke(descriptor);
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Credentials of a key no Host trusts.</summary>
    public static SigningCredentials Untrusted() =>
        Signing(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)));

    private static SigningCredentials Signing(SymmetricSecurityKey key) =>
        new(key, SecurityAlgorithms.HmacSha256);
}
