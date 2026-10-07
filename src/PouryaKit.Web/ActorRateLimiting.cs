using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Web;

// The global limiter of ASP.NET Core's rate limiting, partitioned by the Actor the Kit reads from the authenticated
// caller's claims. A rejection sets 429 and writes nothing, so the status code pages AddPouryaKitWeb adds answer it as
// the same problem response as every other failure.
internal sealed class ActorRateLimiting : IValidateOptions<PouryaKitRateLimitOptions>
{
    // Every unauthenticated caller, and an authenticated one without the Actor claim, which the endpoint refuses; an
    // Actor id is never empty, so no Actor shares this partition.
    private const string Anonymous = "";

    public static void Add(IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services
            .AddOptions<RateLimiterOptions>()
            .Configure<IOptions<PouryaKitRateLimitOptions>, IOptions<PouryaKitWebOptions>>(
                (limiter, configured, web) =>
                {
                    var limits = configured.Value;
                    if (!limits.Enabled)
                    {
                        return;
                    }

                    var actorClaim = web.Value.ActorClaim;
                    limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            Partition(httpContext.User, actorClaim),
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
            ServiceDescriptor.Singleton<IValidateOptions<PouryaKitRateLimitOptions>, ActorRateLimiting>()
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
            failures.Add($"The rate limit's PermitLimit is {options.PermitLimit}; it must be at least 1.");
        }

        if (options.Window <= TimeSpan.Zero)
        {
            failures.Add($"The rate limit's Window is {options.Window}; it must be positive.");
        }

        if (options.QueueLimit < 0)
        {
            failures.Add($"The rate limit's QueueLimit is {options.QueueLimit}; it must not be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Partition(ClaimsPrincipal caller, string actorClaim) =>
        caller.Identity?.IsAuthenticated == true && caller.FindFirstValue(actorClaim) is { } id && !string.IsNullOrWhiteSpace(id)
            ? id
            : Anonymous;
}
