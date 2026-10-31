using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace PouryaKit.Realtime;

// A push is accepted only where G.2 puts it, in a Notification Handler's delivery, which the delivery step records in
// the delivery's own scope, as the Integration Event publisher reads it, and which runs in a Unit of Work of its own
// (ADR 0012); a Host with no delivery step records none, and pushes nothing. pg_notify is queued in the transaction:
// PostgreSQL delivers it to every listener at the commit, and drops it at a rollback, a failure of the commit included.
// A delivery whose commit was lost before its acknowledgement is handed out again, finds its Receipt and runs nothing,
// so it pushes nothing twice.
internal sealed class RealtimePublisher(
    Operation operation,
    IUnitOfWorkConnection unitOfWork,
    IOptions<JsonOptions> json,
    INotificationDelivery? delivery = null
) : IRealtimePublisher
{
    public async Task<Result> PublishAsync<TEvent>(
        string channel,
        TEvent realtimeEvent,
        CancellationToken cancellationToken
    )
        where TEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(realtimeEvent);
        if (
            channel is RealtimeEvents.Connected or RealtimeEvents.Gap
            || channel.AsSpan().ContainsAny('\r', '\n')
        )
        {
            throw new ArgumentException(
                $"The channel {channel} holds a line break or is named as one of the Kit's own events, {RealtimeEvents.Connected} and {RealtimeEvents.Gap}. Name the channel otherwise.",
                nameof(channel)
            );
        }

        if (!operation.MayChangeState)
        {
            throw new InvalidOperationException(
                $"Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, pushed {realtimeEvent.GetType().Name}. Push a realtime event from a Notification Handler."
            );
        }

        if (
            delivery?.Notification is null
            || operation.Context is not { } context
            || !operation.IsUnitOfWorkActive
        )
        {
            throw new InvalidOperationException(
                $"{realtimeEvent.GetType().Name} was pushed outside a Notification Handler's delivery. Push a realtime event from a Notification Handler, so it is sent with that delivery's commit."
            );
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new RealtimeNotification(
                channel,
                context.Tenant ?? OperationContext.NoTenant,
                realtimeEvent
            ),
            json.Value.SerializerOptions
        );
        if (payload.Length >= RealtimeNotification.Limit)
        {
            return RealtimeErrors.PayloadTooLarge;
        }

        var (connection, transaction) = unitOfWork.Join();
        await using var notify = connection.CreateCommand();
        notify.Transaction = transaction;
        notify.CommandText = "select pg_notify(@channel, @payload)";
        notify.Parameters.Add(
            new NpgsqlParameter<string>("channel", RealtimeNotification.PostgresChannel)
        );
        notify.Parameters.Add(
            new NpgsqlParameter<string>("payload", Encoding.UTF8.GetString(payload))
        );
        await notify.ExecuteNonQueryAsync(cancellationToken);
        return Result.Success();
    }
}
