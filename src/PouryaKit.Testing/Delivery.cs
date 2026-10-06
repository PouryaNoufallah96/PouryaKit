namespace PouryaKit.Testing;

/// <summary>
/// One delivery the message store holds: a message, under its <paramref name="WireName"/>, to the subscription
/// <paramref name="Subscription"/>. It was either handled or, once its retries ran out, set aside as a dead letter.
/// </summary>
public sealed record Delivery(string Subscription, string WireName, bool DeadLettered);
