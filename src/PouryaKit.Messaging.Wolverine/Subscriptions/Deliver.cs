using System.Diagnostics;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Messaging.Wolverine;

// One delivery attempt, run as the Operation of the scope the delivery step opened: through the dispatcher, so it gets
// the subscribing Module's Unit of Work, the Operation's clock, and everything a command gets, and a nested command or
// a Domain Event the handler causes joins it. MessageId is the Notification's, Integration Event's or Snapshot's
// identity, the same in every delivery of it; Snapshot is the aggregate and version a Snapshot is of.
internal sealed record Deliver(Subscription Subscription, object Message, Guid MessageId, SnapshotOf? Snapshot = null) : Command;

/// <summary>The aggregate a Snapshot is of, by its key, and the version of it the Snapshot carries.</summary>
internal sealed record SnapshotOf(string AggregateId, long Version);

internal sealed class DeliverHandler(
    IServiceProvider scope,
    Operation operation,
    NotificationDelivery delivery,
    IReceipts? receipts = null,
    HostComposition? composition = null
) : CommandHandler<Deliver>
{
    // Set on the activity of a delivery's Operation, which the dispatcher tags with Deliver or RunDelayedCommand alone:
    // the subscription's or delayed command's declared name, one of a fixed set. The duration histogram keeps its tags.
    public const string SubscriptionTag = "pouryakit.subscription";

    public override async Task<Result> Handle(Deliver command, CancellationToken cancellationToken)
    {
        Activity.Current?.SetTag(SubscriptionTag, command.Subscription.Name);
        // Before the Receipt, so the delivery is retried, then set aside as a dead letter naming the Error, and before
        // the handler, whatever it writes with: a tenant-scoped Module's rows all belong to a tenant. The tenant
        // requirement covers the Module's own commands and queries, never this one.
        if (operation.Context?.Tenant is null && composition?.IsTenantScoped(command.Subscription.Module) is true)
        {
            return TenantErrors.Required;
        }

        // Recorded before the handler runs, in the same transaction: a redelivery after the commit finds it and does
        // nothing, and a concurrent one waits for this one to commit or roll back.
        if (
            receipts is not null
            && !await receipts.RecordAsync(command.Subscription.Name, command.MessageId, cancellationToken)
        )
        {
            return Result.Success();
        }

        // The Receipt stops a duplicate; the version stops an older Snapshot that arrives late or concurrently.
        if (
            command.Snapshot is { } snapshot
            && !await (
                receipts
                ?? throw new InvalidOperationException(
                    $"The subscription {command.Subscription.Name} receives Snapshots, which are applied only when newer, but the Host keeps no Receipts. Call UsePouryaKitWolverine(connectionName) of PouryaKit.Messaging.Wolverine.EFCore on the Host."
                )
            ).RecordVersionAsync(command.Subscription.Name, snapshot.AggregateId, snapshot.Version, cancellationToken)
        )
        {
            return Result.Success();
        }

        // What a Notification Handler publishes or pushes follows from the Domain Event it handles, in this scope alone, and
        // while it runs alone: the Unit of Work drains the Domain Events it raised in this scope once it returns, and their
        // Domain Event Handlers publish and push nothing.
        delivery.Notification = command.Message as DomainEvent;
        try
        {
            return await command.Subscription.InvokeAsync(scope, command.Message, cancellationToken);
        }
        finally
        {
            delivery.Notification = null;
        }
    }
}
