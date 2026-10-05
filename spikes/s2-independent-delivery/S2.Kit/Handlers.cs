namespace S2.Kit;

/// <summary>Spike stand-in for the Kit's Result: success, or a failure carrying an Error.</summary>
public sealed record Result(string? Error)
{
    public bool IsFailure => Error is not null;
    public static Result Success() => new((string?)null);
    public static Result Failure(string error) => new(error);
}

/// <summary>
/// Spike stand-in for the Kit's Integration Event Handler: a plain class in the receiving Module that applies
/// the business effect and returns a Result. It knows nothing about Wolverine, Receipts or acknowledgment.
/// </summary>
public interface IIntegrationEventHandler<in TMessage>
{
    Task<Result> Handle(TMessage message, CancellationToken ct);
}

/// <summary>Raised by a delivery adapter when a handler returned a failed Result, so the transport retries it.</summary>
public sealed class HandlerFailedException(string subscription, string error)
    : Exception($"Handler for subscription '{subscription}' returned a failed Result: {error}");
