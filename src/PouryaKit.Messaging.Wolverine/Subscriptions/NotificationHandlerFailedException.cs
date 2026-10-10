using System.Collections.Immutable;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Thrown when a Notification delivery's Operation fails, whether its handler returned a failed Result or nested work
/// made it rollback-only, so the delivery is retried as if the handler had thrown: a failed Result is never acknowledged.
/// Nothing the attempt changed was committed. Its message names each Error, so a dead letter tells why.
/// </summary>
public sealed class NotificationHandlerFailedException(string subscription, ImmutableArray<Error> errors)
    : Exception(
        $"The Notification Handler of the subscription {subscription} failed with {string.Join(", ", errors.Select(error => $"{error.Code} ({error.Message})"))}; the delivery is retried, then set aside as a dead letter."
    )
{
    /// <summary>The name of the subscription whose Notification Handler failed.</summary>
    public string Subscription { get; } = subscription;

    /// <summary>The Errors of the failed Result, in the order given.</summary>
    public ImmutableArray<Error> Errors { get; } = errors;
}
