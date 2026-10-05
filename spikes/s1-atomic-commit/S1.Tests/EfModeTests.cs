using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Persistence;

namespace S1.Tests;

/// <summary>Objective 3: Wolverine's Eager and Lightweight EF modes, with no retrying execution strategy configured.</summary>
public sealed class EfModeTests(PostgresServer server)
{
    private static readonly TimeSpan DeliveryBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The mode reaches only handlers that use Wolverine's generated EF transactional middleware.
    /// The owned Unit of Work is covered in both modes by
    /// <see cref="AtomicCommitTests.Commit_writes_business_row_and_envelope_row_in_one_transaction"/>.
    /// </summary>
    [Theory]
    [InlineData(TransactionMiddlewareMode.Eager, EnvelopeMapping.Raw)]
    [InlineData(TransactionMiddlewareMode.Lightweight, EnvelopeMapping.Raw)]
    [InlineData(TransactionMiddlewareMode.Eager, EnvelopeMapping.MappedToStoreSchema)]
    [InlineData(TransactionMiddlewareMode.Lightweight, EnvelopeMapping.MappedToStoreSchema)]
    public async Task Middleware_handler_effect_and_inbox_mark_share_a_transaction_only_in_eager_mode(
        TransactionMiddlewareMode mode, EnvelopeMapping mapping)
    {
        await using var db = await server.CreateDatabaseAsync();
        using var host = db.BuildHost(mode, mapping);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var id = Guid.NewGuid();

        await host.Services.GetRequiredService<IMessageBus>().PublishAsync(new CreateProductThroughMiddleware(id));

        Assert.True(await Eventually.WaitAsync(async () => await db.ProductsAsync(id) == 1, DeliveryBudget));
        Assert.True(await Eventually.WaitAsync(async () => (await db.IncomingEnvelopesAsync()).Any(e => e.Status == "Handled"), DeliveryBudget));

        var transactionOpenAtEntry = CreateProductThroughMiddlewareHandler.TransactionOpenAtEntry[id];
        var productCommittedAt = await db.CommittedAtAsync("catalog.products", id);
        var handled = (await db.IncomingEnvelopesAsync()).Single();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"mode={mode} mapping={mapping} txAtEntry={transactionOpenAtEntry} " +
            $"productCommittedAt={productCommittedAt:O} handledCommittedAt={handled.CommittedAt:O}");

        // Eager: a transaction is open when the handler runs, and the effect and the inbox "Handled" mark
        // commit together. Lightweight: no transaction at entry; SaveChanges commits the effect alone and
        // the mark is written afterwards by a separate transaction.
        var eager = mode == TransactionMiddlewareMode.Eager;
        Assert.Equal(eager, transactionOpenAtEntry);
        Assert.Equal(eager, productCommittedAt == handled.CommittedAt);
    }
}
