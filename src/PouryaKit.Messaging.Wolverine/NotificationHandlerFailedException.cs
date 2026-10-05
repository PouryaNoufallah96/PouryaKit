using System.Collections.Immutable;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Thrown by a delivery whose Notification Handler returned a failed Result, so the delivery is retried as if the
/// handler had thrown: a failed Result is never acknowledged. Nothing the attempt changed was committed. Its message
/// names each Error, so a dead letter tells why.
/// </summary>
public sealed class NotificationHandlerFailedException(string subscription, ImmutableArray<Error> errors)
    : Exception(
        $"The Notification Handler of the subscription {subscription} failed with {string.Join(", ", errors.Select(error => $"{error.Code} ({error.Message})"))}; the delivery is retried, then set aside as a dead letter."
    )
{
    public string Subscription { get; } = subscription;

    public ImmutableArray<Error> Errors { get; } = errors;
}
