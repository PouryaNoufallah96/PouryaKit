using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Cqrs;

/// <summary>Composes the idempotency key options of the Commands, queries and validation Capability on a Host's services.</summary>
public static class IdempotencyServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Checks <see cref="PouryaKitIdempotencyOptions"/> when the Host starts, once however many registrations call it,
        /// and fails the start naming each value out of range. For a persistence package's registration that keeps the
        /// keys of <c>[Idempotent]</c> commands and removes them as the options say, such as <c>AddPouryaKitEfCore</c>;
        /// Feature code never calls it.
        /// </summary>
        public IServiceCollection AddPouryaKitIdempotencyOptions()
        {
            ArgumentNullException.ThrowIfNull(services);
            services.AddOptions<PouryaKitIdempotencyOptions>().ValidateOnStart();
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<PouryaKitIdempotencyOptions>, IdempotencyOptionsValidation>()
            );
            return services;
        }
    }
}

internal sealed class IdempotencyOptionsValidation : IValidateOptions<PouryaKitIdempotencyOptions>
{
    // What PeriodicTimer accepts, and a retention whose cutoff stays far from DateTimeOffset.MinValue.
    private static readonly TimeSpan MinCleanupInterval = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxCleanupInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private static readonly TimeSpan MaxRetention = TimeSpan.FromDays(36500);

    public ValidateOptionsResult Validate(string? name, PouryaKitIdempotencyOptions options)
    {
        List<string> failures = [];
        if ((options.Retention <= TimeSpan.Zero || options.Retention > MaxRetention) && options.Retention != Timeout.InfiniteTimeSpan)
        {
            failures.Add(
                $"The idempotency key Retention is {options.Retention}; it must be positive and at most {MaxRetention}, or Timeout.InfiniteTimeSpan to keep every key."
            );
        }

        if (options.CleanupInterval < MinCleanupInterval || options.CleanupInterval > MaxCleanupInterval)
        {
            failures.Add(
                $"The idempotency key CleanupInterval is {options.CleanupInterval}; it must be from {MinCleanupInterval} to {MaxCleanupInterval}."
            );
        }

        if (options.BatchSize < 1)
        {
            failures.Add($"The idempotency key BatchSize is {options.BatchSize}; it must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
