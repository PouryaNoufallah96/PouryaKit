namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// The wire names under which a Host's Integration Events and Snapshots cross to and from other Hosts, for a transport
/// package such as <c>PouryaKit.Messaging.Wolverine.RabbitMq</c>; application code never needs it. Read it once
/// Wolverine's options are built, when every Composition Root has declared its publications and subscriptions.
/// </summary>
public interface IWireNames
{
    /// <summary>
    /// Every Integration Event and Snapshot a Module of this Host publishes, with its wire name, whether or not a
    /// subscription of this Host receives it.
    /// </summary>
    IReadOnlyDictionary<Type, string> Published { get; }

    /// <summary>
    /// The wire name of every Integration Event and Snapshot another Host publishes (<c>Receive</c>,
    /// <c>ReceiveSnapshot</c>) that a subscription of this Host receives; never one a Module of this Host publishes,
    /// which its subscriptions here receive from the publication itself.
    /// </summary>
    IReadOnlyCollection<string> Received { get; }
}
