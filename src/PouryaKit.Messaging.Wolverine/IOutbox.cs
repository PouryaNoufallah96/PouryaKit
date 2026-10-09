namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Marks that the Host has an Outbox, and has nothing to call: registered by the package that writes Notifications,
/// Integration Events, Snapshots and delayed or recurring commands to an Outbox in the Unit of Work's commit, such as
/// <c>PouryaKit.Messaging.Wolverine.EFCore</c>. A Host
/// that declares Notification Handlers, Integration Event Handlers, published Integration Events or Snapshots, or
/// delayed or recurring commands without one fails when it is built: its Operations would commit and nothing would
/// ever store or deliver what they published or scheduled. A Host with no database, which only receives messages,
/// declares none of them.
/// </summary>
public interface IOutbox;
