using PouryaKit.Cqrs;

namespace PouryaKit.Messaging.Wolverine;

/// <summary>
/// Schedules a command of the running Operation's Module to run later, as a continuation of the Operation: a command's
/// handler, a Domain Event Handler or a Notification Handler injects it. Nothing is sent from here. The Unit of Work
/// writes the command to the Module's Outbox in the same commit as the Operation's change, so a failed or rolled-back
/// Operation leaves none, and the message store keeps it until it is due, across Host restarts. It then runs through the
/// dispatcher, in the Module, with the scheduling Operation's Actor, tenant and correlation, caused by that Operation,
/// and records its Receipt in the same Unit of Work, so a redelivered or replayed delivery applies its effect once. A
/// delivery whose command fails is retried, then set aside as a dead letter, as a Notification is.
/// </summary>
public interface IDelayedCommands
{
    /// <summary>
    /// Schedules <paramref name="command"/> to run at <paramref name="at"/>, or as soon as the Operation commits when
    /// that time has passed. The message store compares it with the system clock, not with the Host's
    /// <see cref="TimeProvider"/>. Throws where the Operation only reads (in a query, an authorizer or a validator),
    /// and when the running Operation's Module did not declare the command's type with
    /// <c>AddPouryaKitDelayedCommand</c>.
    /// </summary>
    void Schedule(Command command, DateTimeOffset at);
}
