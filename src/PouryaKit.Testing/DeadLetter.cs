namespace PouryaKit.Testing;

/// <summary>
/// A delivery the message store set aside once its retries ran out: a message, under its <paramref name="WireName"/>,
/// to the subscription <paramref name="Subscription"/>, with the exception of its last attempt. A handler that returned a
/// failed Result failed with the Kit's <c>…HandlerFailedException</c>, whose message names each Error. One that failed at
/// the Host's broker queue, before it reached any subscription, such as a wire name the Host no longer receives, names
/// that queue's address instead, such as <c>rabbitmq://queue/inventory</c>; replaying it hands it to that queue again.
/// </summary>
public sealed record DeadLetter(Guid Id, string Subscription, string WireName, string ExceptionType, string ExceptionMessage);
