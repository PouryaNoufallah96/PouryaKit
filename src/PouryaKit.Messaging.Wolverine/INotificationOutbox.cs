namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Registered by the package that writes Notifications and Integration Events to an Outbox in the Unit of Work's
/// commit, such as <c>PouryaKit.Messaging.Wolverine.EFCore</c>. A Host that declares Notification Handlers, Integration
/// Event Handlers or published Integration Events without one fails when it is built: its Operations would commit and
/// nothing would ever store or deliver what they published. A Host with no database, which only receives messages,
/// declares none of them.
/// </summary>
public interface INotificationOutbox;
