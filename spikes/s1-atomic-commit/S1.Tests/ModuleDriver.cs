using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace S1.Tests;

public enum ModuleName { Catalog, Inventory }

/// <summary>Runs the same Operation shape against either Module: insert one aggregate row, publish one Notification.</summary>
public sealed class ModuleDriver(IHost host, SpikeDatabase db, ModuleName module)
{
    public string BusinessTable => module == ModuleName.Catalog ? "catalog.products" : "inventory.stock_items";
    public string MessageType => module == ModuleName.Catalog ? typeof(ProductCreated).FullName! : typeof(StockItemCreated).FullName!;

    public Task<long> BusinessRowsAsync(Guid id) =>
        module == ModuleName.Catalog ? db.ProductsAsync(id) : db.StockItemsAsync(id);

    public Task<string?> BusinessXminAsync(Guid id) =>
        db.ScalarAsync<string>($"select xmin::text from {BusinessTable} where id = $1", id);

    /// <summary>Inserts a row directly, outside the Unit of Work (to provoke a duplicate key).</summary>
    public async Task SeedAsync(Guid id)
    {
        var sql = module == ModuleName.Catalog
            ? "insert into catalog.products(id, name) values ($1, 'seed')"
            : "insert into inventory.stock_items(id, quantity) values ($1, 1)";
        await db.CountAsync(sql + " returning 1", id);
    }

    /// <param name="publishThroughPlainBus">Publish through the scope's <see cref="IMessageBus"/> instead of the outbox.</param>
    public async Task<Outcome> RunAsync(Guid id, Outcome result = Outcome.Succeeded, CommitHooks? hooks = null,
        bool publishThroughPlainBus = false)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        if (module == ModuleName.Catalog)
        {
            var uow = scope.ServiceProvider.GetRequiredService<SpikeUnitOfWork<CatalogDbContext>>();
            return await uow.ExecuteAsync(async (ctx, outbox) =>
            {
                ctx.Products.Add(new Product { Id = id, Name = "p" });
                await (publishThroughPlainBus ? bus : outbox).PublishAsync(new ProductCreated(id));
                return result;
            }, hooks);
        }
        else
        {
            var uow = scope.ServiceProvider.GetRequiredService<SpikeUnitOfWork<InventoryDbContext>>();
            return await uow.ExecuteAsync(async (ctx, outbox) =>
            {
                ctx.StockItems.Add(new StockItem { Id = id, Quantity = 1 });
                await (publishThroughPlainBus ? bus : outbox).PublishAsync(new StockItemCreated(id));
                return result;
            }, hooks);
        }
    }
}
