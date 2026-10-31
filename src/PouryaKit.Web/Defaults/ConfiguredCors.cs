using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Web;

// Registers each configured policy with ASP.NET Core's CORS services. Origins are only ever listed (WithOrigins), and
// the validation refuses "*", which CorsPolicy would read as any origin.
internal sealed class ConfiguredCors : IValidateOptions<PouryaKitCorsOptions>
{
    public static void Add(IServiceCollection services)
    {
        services.AddCors();
        services
            .AddOptions<CorsOptions>()
            .Configure<IOptions<PouryaKitCorsOptions>>(
                (cors, configured) =>
                {
                    foreach (var (name, policy) in configured.Value.Policies)
                    {
                        cors.AddPolicy(name, builder => Build(builder, policy));
                    }
                }
            );
        services.AddOptions<PouryaKitCorsOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PouryaKitCorsOptions>, ConfiguredCors>()
        );
    }

    public ValidateOptionsResult Validate(string? name, PouryaKitCorsOptions options)
    {
        List<string> failures = [];
        foreach (var (policyName, policy) in options.Policies)
        {
            failures.AddRange(
                policy
                    .Origins.Where(origin => !IsOrigin(origin))
                    .Select(origin =>
                        $"The CORS policy {policyName} lists the origin '{origin}', which is not scheme://host[:port] as a browser sends it, or is a wildcard."
                    )
            );
            failures.AddRange(
                policy
                    .Methods.Concat(policy.Headers)
                    .Concat(policy.ExposedHeaders)
                    .Where(value =>
                        string.IsNullOrWhiteSpace(value)
                        || value.Contains('*', StringComparison.Ordinal)
                    )
                    .Select(value =>
                        $"The CORS policy {policyName} lists '{value}' as a method or header; name each one, never a wildcard."
                    )
            );
            if (policy.PreflightMaxAge < TimeSpan.Zero)
            {
                failures.Add($"The CORS policy {policyName} has a negative PreflightMaxAge.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Build(CorsPolicyBuilder builder, PouryaKitCorsPolicy policy)
    {
        builder
            .WithOrigins([.. policy.Origins])
            .WithMethods([.. policy.Methods])
            .WithHeaders([.. policy.Headers])
            .WithExposedHeaders([.. policy.ExposedHeaders]);
        if (policy.AllowCredentials)
        {
            builder.AllowCredentials();
        }

        if (policy.PreflightMaxAge is { } maxAge)
        {
            builder.SetPreflightMaxAge(maxAge);
        }
    }

    private static bool IsOrigin(string origin) =>
        !origin.Contains('*', StringComparison.Ordinal)
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.Equals(
            uri.GetLeftPart(UriPartial.Authority),
            origin,
            StringComparison.OrdinalIgnoreCase
        );
}
