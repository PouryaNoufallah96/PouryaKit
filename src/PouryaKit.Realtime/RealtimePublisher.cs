using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace PouryaKit.Realtime;

// A Notification Handler's delivery runs in a Unit of Work of its own, as a trusted continuation of the Operation that
// raised the Notification, restored from the headers its Outbox wrote; that is all the Operation shows of it. So a push
// is accepted where those hold, which also admits work that continues an Operation as a Notification Handler does: an
// Integration Event Handler's or a delayed command's delivery, a trusted peer's request, and a command another Module's
// handler sends with Continue(). Each commits or rolls back with what it pushed, which is the guarantee (ADR 0012).
// pg_notify is queued in the transaction: PostgreSQL delivers it to every listener at the commit, and drops it at a
// rollback, a failure of the commit included. A delivery whose commit was lost before its acknowledgement is handed out
// again, finds its Receipt and runs nothing, so it pushes nothing twice.
internal sealed class RealtimePublisher(Operation operation, IUnitOfWorkConnection unitOfWork, IOptions<JsonOptions> json)
    : IRealtimePublisher
{
    public async Task<Result> PublishAsync<TEvent>(string channel, TEvent realtimeEvent, CancellationToken cancellationToken)
        where TEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(realtimeEvent);
        if (channel is RealtimeEvents.Connected or RealtimeEvents.Gap || channel.AsSpan().ContainsAny('\r', '\n'))
        {
            throw new ArgumentException(
                $"The channel {channel} holds a line break or is named as one of the Kit's own events, {RealtimeEvents.Connected} and {RealtimeEvents.Gap}. Name the channel otherwise.",
                nameof(channel)
            );
        }

        if (operation.Context is not { IsTrustedContinuation: true } context || !operation.IsUnitOfWorkActive || !operation.MayChangeState)
        {
            throw new InvalidOperationException(
                $"{realtimeEvent.GetType().Name} was pushed outside a Notification Handler's delivery. Push a realtime event from a Notification Handler, so it is sent with that delivery's commit."
            );
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new RealtimeNotification(channel, context.Tenant ?? OperationContext.NoTenant, realtimeEvent),
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
        notify.Parameters.Add(new NpgsqlParameter<string>("channel", RealtimeNotification.PostgresChannel));
        notify.Parameters.Add(new NpgsqlParameter<string>("payload", Encoding.UTF8.GetString(payload)));
        await notify.ExecuteNonQueryAsync(cancellationToken);
        return Result.Success();
    }
}
