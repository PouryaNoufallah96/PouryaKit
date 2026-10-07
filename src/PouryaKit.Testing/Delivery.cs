namespace PouryaKit.Testing;

/// <summary>
/// One delivery the message store holds: a message, under its <paramref name="WireName"/>, to the subscription
/// <paramref name="Subscription"/>. It was either handled or, once its retries ran out, set aside as a dead letter. One
/// received from another Host at the Host's broker queue, before it was published again to each subscription's local
/// queue, names that queue's address instead, such as <c>rabbitmq://queue/inventory</c>.
/// </summary>
public sealed record Delivery(string Subscription, string WireName, bool DeadLettered);
