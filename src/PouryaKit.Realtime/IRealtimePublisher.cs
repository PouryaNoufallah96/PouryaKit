using PouryaKit.Domain;

namespace PouryaKit.Realtime;

/// <summary>
/// Pushes realtime events to browser clients. A Notification Handler injects it and pushes a small fact of the
/// Notification it handles, or a reference a client then reads through a query, on a Channel that an SSE endpoint mapped
/// with <c>MapPouryaKitRealtime</c> admits. Nothing is sent from here: the event is written as a PostgreSQL notification
/// on the delivery's own transaction, which PostgreSQL hands to the listener of every instance of the Host only when that
/// transaction commits, so a delivery that rolls back pushes nothing, and each instance passes the event to its clients
/// of the delivery's tenant that listen on the Channel. A client receives it at most once, and only while connected:
/// nothing is stored for a client that connects later.
/// </summary>
public interface IRealtimePublisher
{
    /// <summary>
    /// Pushes <paramref name="realtimeEvent"/>, a plain record of the Module's Contracts, on <paramref name="channel"/>
    /// for the delivery's tenant, written with the Host's HTTP JSON options, which <c>AddPouryaKitWeb</c> sets to the
    /// Kit's JSON. Fails with <see cref="RealtimeErrors.PayloadTooLarge"/>, pushing nothing, when the notification, the
    /// Channel, the tenant and the event together, is not shorter than the 8000 bytes PostgreSQL carries. Throws, as the
    /// Integration Event publisher does, when called outside a Notification Handler's delivery, which the Operation
    /// Context records as its <c>Notification</c>, or where the Operation only reads; and on a blank Channel, one holding a
    /// line break, or one named as the Kit's own events (<see cref="RealtimeEvents"/>).
    /// </summary>
    Task<Result> PublishAsync<TEvent>(string channel, TEvent realtimeEvent, CancellationToken cancellationToken)
        where TEvent : class;
}
