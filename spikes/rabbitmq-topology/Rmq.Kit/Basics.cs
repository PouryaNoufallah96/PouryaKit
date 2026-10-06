using System.Collections.Concurrent;
using Wolverine.Util;

namespace Rmq.Kit;

/// <summary>Spike stand-in for the Kit's Result: success, or a failure carrying an Error.</summary>
public sealed record Result(string? Error)
{
    public bool IsFailure => Error is not null;

    public static Result Success() => new((string?)null);

    public static Result Failure(string error) => new(error);
}

/// <summary>
/// Spike stand-in for the Kit's Integration Event Handler: a plain class in the receiving Module that applies the
/// business effect and returns a Result. It may be internal; it knows nothing about Wolverine, Receipts or the broker.
/// </summary>
public interface IIntegrationEventHandler<in TMessage>
{
    Task<Result> Handle(TMessage message, CancellationToken cancellationToken);
}

/// <summary>Raised by the delivery step when a handler returned a failed Result, so Wolverine retries the delivery.</summary>
public sealed class HandlerFailedException(string subscription, string error)
    : Exception($"The handler of subscription '{subscription}' returned a failed Result: {error}");

/// <summary>The Kit's header names, as in PouryaKit.Messaging.</summary>
public static class KitHeaders
{
    /// <summary>The Domain Event's id an Integration Event follows from: the Receipt's identity, the same in every delivery.</summary>
    public const string DomainEventId = "pouryakit-domain-event-id";
}

/// <summary>
/// Stable wire names independent of CLR type names. Wolverine's naming strategies are process-global statics, so this
/// registry is too; both Hosts of a test process declare the same name for a type.
/// </summary>
public static class WireNames
{
    private static readonly ConcurrentDictionary<Type, string> Names = new();

    public static void Register(Type messageType, string wireName)
    {
        if (Names.GetOrAdd(messageType, wireName) != wireName)
        {
            throw new InvalidOperationException($"{messageType} already has the wire name {Names[messageType]}.");
        }
    }

    public static string Of(Type messageType) => Names[messageType];

    public static void UseInWolverine() => WolverineMessageNaming.InsertFirst<Naming>();

    public sealed class Naming : IMessageTypeNaming
    {
        public bool TryDetermineName(Type messageType, out string messageTypeName) =>
            Names.TryGetValue(messageType, out messageTypeName!);
    }
}
