using System.Collections.Concurrent;
using Npgsql;
using Wolverine;
using Wolverine.Attributes;

namespace S1;

/// <summary>
/// Stand-in for a Notification Handler: records each delivery in <c>spike.deliveries</c> with raw SQL,
/// so delivery is observable from the database and from another process.
/// </summary>
public static class ProductCreatedHandler
{
    public static async Task Handle(ProductCreated message, Envelope envelope, NpgsqlDataSource db)
    {
        await DeliveryLog.RecordAsync(db, nameof(ProductCreatedHandler), message.ProductId, envelope.Id);
        await HandlerGate.WaitIfBlockedAsync();
    }
}

public static class StockItemCreatedHandler
{
    public static async Task Handle(StockItemCreated message, Envelope envelope, NpgsqlDataSource db)
    {
        await DeliveryLog.RecordAsync(db, nameof(StockItemCreatedHandler), message.StockItemId, envelope.Id);
        await HandlerGate.WaitIfBlockedAsync();
    }
}

/// <summary>Message for the EF mode comparison: handled with Wolverine's EF transactional middleware.</summary>
public sealed record CreateProductThroughMiddleware(Guid ProductId);

public static class CreateProductThroughMiddlewareHandler
{
    /// <summary>Whether a database transaction was already open when the handler body ran, keyed by product id.</summary>
    public static readonly ConcurrentDictionary<Guid, bool> TransactionOpenAtEntry = new();

    [Transactional]
    public static void Handle(CreateProductThroughMiddleware message, CatalogDbContext db)
    {
        TransactionOpenAtEntry[message.ProductId] = db.Database.CurrentTransaction is not null;
        db.Products.Add(new Product { Id = message.ProductId, Name = "via middleware" });
    }
}

public static class DeliveryLog
{
    public static async Task RecordAsync(NpgsqlDataSource db, string handler, Guid entityId, Guid messageId)
    {
        await using var cmd = db.CreateCommand(
            "insert into spike.deliveries(handler, entity_id, message_id) values ($1, $2, $3)");
        cmd.Parameters.AddWithValue(handler);
        cmd.Parameters.AddWithValue(entityId);
        cmd.Parameters.AddWithValue(messageId);
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Lets the crash child process hold a handler open after it has recorded its delivery.</summary>
public static class HandlerGate
{
    public static bool BlockForever { get; set; }

    public static Task WaitIfBlockedAsync() => BlockForever ? Task.Delay(Timeout.Infinite) : Task.CompletedTask;
}
