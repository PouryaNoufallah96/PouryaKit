using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Web;

// The global limiter of ASP.NET Core's rate limiting, partitioned by the Actor the request's Operation runs as, which
// HttpCallerContext resolves for both. A rejection sets 429 and writes nothing, so the status code pages AddPouryaKitWeb adds answer it as
// the same problem response as every other failure.
internal sealed class ActorRateLimiting : IValidateOptions<PouryaKitRateLimitOptions>
{
    // A request no Actor resolves for: an unauthenticated caller of an endpoint that names no anonymous Actor, or a caller
    // without the Actor claim, which a Module's endpoint refuses. Every Actor's key names its kind, so none is empty.
    private const string Unresolved = "";

    public static void Add(IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services
            .AddOptions<RateLimiterOptions>()
            .Configure<
                IOptions<PouryaKitRateLimitOptions>,
                IOptions<PouryaKitWebOptions>,
                IOptions<PouryaKitTrustedPeerOptions>
            >(
                (limiter, configured, web, trusted) =>
                {
                    var limits = configured.Value;
                    if (!limits.Enabled)
                    {
                        return;
                    }

                    var options = web.Value;
                    var peers = trusted.Value.Peers;
                    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                        httpContext =>
                            RateLimitPartition.GetFixedWindowLimiter(
                                Partition(httpContext, options, peers),
                                _ => new FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = limits.PermitLimit,
                                    Window = limits.Window,
                                    QueueLimit = limits.QueueLimit,
                                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                                }
                            )
                    );
                    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                }
            );
        services.AddOptions<PouryaKitRateLimitOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<PouryaKitRateLimitOptions>,
                ActorRateLimiting
            >()
        );
    }

    public ValidateOptionsResult Validate(string? name, PouryaKitRateLimitOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];
        if (options.PermitLimit < 1)
        {
            failures.Add(
                $"The rate limit's PermitLimit is {options.PermitLimit}; it must be at least 1."
            );
        }

        if (options.Window <= TimeSpan.Zero)
        {
            failures.Add($"The rate limit's Window is {options.Window}; it must be positive.");
        }

        if (options.QueueLimit < 0)
        {
            failures.Add(
                $"The rate limit's QueueLimit is {options.QueueLimit}; it must not be negative."
            );
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    // ASP.NET Core's AuthenticationMiddleware sets IAuthenticationFeature on every request it sees, anonymous ones too, so
    // in a Host that registers authentication its absence means authentication has not run yet and every caller would
    // look anonymous: one bucket for all. A Host without authentication has only anonymous callers, partitioned by the
    // Actor each endpoint names. Routing has no such mark, since it sets nothing on a request no endpoint matches, so a
    // limiter before UseRouting is not detected; it puts the anonymous callers of a public endpoint in the unresolved
    // partition.
    private static string Partition(
        HttpContext httpContext,
        PouryaKitWebOptions options,
        ISet<string> peers
    )
    {
        if (
            httpContext.Features.Get<IAuthenticationFeature>() is null
            && httpContext.RequestServices.GetService<IAuthenticationSchemeProvider>() is not null
        )
        {
            throw new InvalidOperationException(
                "The rate limit partitions each request by its Actor, but the request reached the rate limiter before authentication ran, so every caller would share one partition. Call app.UseAuthentication() before app.UseRateLimiter(), or leave UseAuthentication to WebApplication, which adds it ahead of the application's own middleware."
            );
        }

        try
        {
            return HttpCallerContext.Resolve(httpContext, options, peers)?.Actor is { } actor
                ? $"{actor.Kind}:{actor.Id}"
                : Unresolved;
        }
        catch (BadHttpRequestException)
        {
            // A trusted peer's unreadable context headers: its endpoint answers 400.
            return Unresolved;
        }
    }
}
