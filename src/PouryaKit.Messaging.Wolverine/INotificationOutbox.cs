namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Registered by the package that writes Notifications to an Outbox in the Unit of Work's commit, such as
/// <c>PouryaKit.Messaging.Wolverine.EFCore</c>. A Host that declares Notification Handlers without one fails when it
/// starts: its Operations would commit and nothing would ever store or deliver their Notifications. A Host with no
/// database, which only receives messages, declares no Notification Handler.
/// </summary>
public interface INotificationOutbox;
