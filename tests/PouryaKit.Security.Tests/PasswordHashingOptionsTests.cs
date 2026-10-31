using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace PouryaKit.Security.Tests;

// The password hashing options follow the options pattern: every AddPouryaKitPasswordHashing call and every
// Configure<T>, a configuration section's included, configure them, and the Host refuses to start with invalid ones.
public sealed class PasswordHashingOptionsTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public void Options_bound_from_a_configuration_section_reach_the_hasher()
    {
        var builder = Builder(
            new() { ["PasswordHashing:Iterations"] = "700000", ["PasswordHashing:SaltSize"] = "32" }
        );
        builder.Services.AddPouryaKitPasswordHashing();
        builder.Services.Configure<PouryaKitPasswordHashingOptions>(
            builder.Configuration.GetSection("PasswordHashing")
        );
        using var host = builder.Build();

        var fields = host.Services.GetRequiredService<IPasswordHasher>().Hash(Password).Split('$');

        Assert.Equal("700000", fields[3]);
        Assert.Equal(32, Convert.FromBase64String(fields[4]).Length);
    }

    // Configure callbacks accumulate, so a second call adds to the first one's settings instead of starting again from
    // the defaults.
    [Fact]
    public void A_second_registration_adds_to_the_first_ones_options_and_keeps_one_hasher()
    {
        var services = new ServiceCollection()
            .AddPouryaKitPasswordHashing(options => options.Iterations = 700_000)
            .AddPouryaKitPasswordHashing(options => options.SaltSize = 32);
        using var provider = services.BuildServiceProvider();

        var fields = provider.GetRequiredService<IPasswordHasher>().Hash(Password).Split('$');

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPasswordHasher));
        Assert.Equal("700000", fields[3]);
        Assert.Equal(32, Convert.FromBase64String(fields[4]).Length);
    }

    [Fact]
    public void Registration_replaces_a_hasher_registered_before_it()
    {
        var services = new ServiceCollection()
            .AddSingleton<IPasswordHasher, PlainHasher>()
            .AddPouryaKitPasswordHashing();
        using var provider = services.BuildServiceProvider();

        var hasher = provider.GetRequiredService<IPasswordHasher>();

        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IPasswordHasher));
        Assert.StartsWith(
            "pbkdf2$1$sha256$600000$",
            hasher.Hash(Password),
            StringComparison.Ordinal
        );
    }

    // The Host builds, since nothing resolves the hasher, and refuses to start.
    [Theory]
    [InlineData(PasswordHashAlgorithm.Sha256, 599_999, 16, 32, "Iterations")]
    [InlineData(PasswordHashAlgorithm.Sha512, 219_999, 16, 32, "Iterations")]
    [InlineData(PasswordHashAlgorithm.Sha256, 10_000_001, 16, 32, "Iterations")]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 15, 32, "SaltSize")]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 65, 32, "SaltSize")]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 16, 31, "KeySize")]
    [InlineData(PasswordHashAlgorithm.Sha256, 600_000, 16, 33, "KeySize")]
    [InlineData(PasswordHashAlgorithm.Sha512, 220_000, 16, 65, "KeySize")]
    [InlineData((PasswordHashAlgorithm)7, 600_000, 16, 32, "Algorithm")]
    public async Task Options_outside_their_bounds_fail_the_host_at_start(
        PasswordHashAlgorithm algorithm,
        int iterations,
        int saltSize,
        int keySize,
        string invalid
    )
    {
        var builder = Builder([]);
        builder.Services.AddPouryaKitPasswordHashing(options =>
            Set(options, algorithm, iterations, saltSize, keySize)
        );
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken)
        );

        Assert.Equal(typeof(PouryaKitPasswordHashingOptions), thrown.OptionsType);
        Assert.Contains(invalid, Assert.Single(thrown.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PasswordHashAlgorithm.Sha256, 10_000_000, 64, 32)]
    [InlineData(PasswordHashAlgorithm.Sha512, 220_000, 16, 64)]
    public async Task Options_at_their_bounds_let_the_host_start(
        PasswordHashAlgorithm algorithm,
        int iterations,
        int saltSize,
        int keySize
    )
    {
        var builder = Builder([]);
        builder.Services.AddPouryaKitPasswordHashing(options =>
            Set(options, algorithm, iterations, saltSize, keySize)
        );
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        var hash = host.Services.GetRequiredService<IPasswordHasher>().Hash(Password);
        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(iterations.ToString(CultureInfo.InvariantCulture), hash.Split('$')[3]);
    }

    private static HostApplicationBuilder Builder(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { DisableDefaults = true }
        );
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static void Set(
        PouryaKitPasswordHashingOptions options,
        PasswordHashAlgorithm algorithm,
        int iterations,
        int saltSize,
        int keySize
    )
    {
        options.Algorithm = algorithm;
        options.Iterations = iterations;
        options.SaltSize = saltSize;
        options.KeySize = keySize;
    }

    private sealed class PlainHasher : IPasswordHasher
    {
        public string Hash(string password) => password;

        public PasswordVerification Verify(string password, string? encodedHash) =>
            password == encodedHash ? PasswordVerification.Succeeded : PasswordVerification.Failed;
    }
}
