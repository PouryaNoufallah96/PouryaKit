namespace PouryaKit.Cqrs;

/// <summary>
/// Marks that the Host has an Outbox, and has nothing to call: a messaging package's store registration registers it
/// beside the Outbox of the write model it serves, <c>UsePouryaKitWolverine(connectionName)</c> of
/// <c>PouryaKit.Messaging.Wolverine.EFCore</c> beside the EF Core Unit of Work's and <c>UsePouryaKitMarten</c> of
/// <c>PouryaKit.Messaging.Wolverine.Marten</c> beside the Marten Unit of Work's. The Kit's composition checks read it when
/// the Host is built. A Host that declares Notification Handlers, Integration Event Handlers, published Integration Events
/// or Snapshots, or delayed or recurring commands without one fails: its Operations would commit and nothing would ever
/// store or deliver what they published or scheduled. A Host with one also fails when a write model it composes has no
/// Outbox of its own: that write model's Operations would commit and never write what they publish. A Host with no
/// database, which only receives messages, declares none of them.
/// </summary>
public interface IOutbox;
