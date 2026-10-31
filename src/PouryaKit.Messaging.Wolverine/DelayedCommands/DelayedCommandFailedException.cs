using System.Collections.Immutable;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Thrown when a delayed command's delivery fails, whether the command returned a failed Result or nested work made it
/// rollback-only, so the delivery is retried as if it had thrown, then set aside as a dead letter. Nothing the attempt
/// changed was committed. Its message names each Error, so a dead letter tells why.
/// </summary>
public sealed class DelayedCommandFailedException(
    string delayedCommand,
    ImmutableArray<Error> errors
)
    : Exception(
        $"The delayed command {delayedCommand} failed with {string.Join(", ", errors.Select(error => $"{error.Code} ({error.Message})"))}; the delivery is retried, then set aside as a dead letter."
    )
{
    /// <summary>The name the delayed command or recurring command that failed is declared under.</summary>
    public string DelayedCommand { get; } = delayedCommand;

    /// <summary>The Errors of the failed Result, in the order given.</summary>
    public ImmutableArray<Error> Errors { get; } = errors;
}
