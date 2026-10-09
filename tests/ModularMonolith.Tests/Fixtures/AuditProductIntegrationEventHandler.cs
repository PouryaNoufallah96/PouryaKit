using System.Collections.Concurrent;
using Catalog.Contracts.Products.Events.V1;
using Dapper;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Fixtures;

// The tests' own second subscription of Inventory.Host to ProductCreatedV1, beside Inventory's: it audits the product
// in Inventory's Unit of Work, and returns a failed Result after writing its audit for a product a test refuses.
internal sealed class AuditProductIntegrationEventHandler(IUnitOfWorkConnection unitOfWork) : IIntegrationEventHandler<ProductCreatedV1>
{
    public static readonly Error Refused = Error.Conflict("Tests.AuditRefused", "The audit was refused after it was written.");

    private static readonly ConcurrentDictionary<Guid, bool> Refusing = new();

    /// <summary>Refuses every audit of <paramref name="product"/> after it is written, until disposed.</summary>
    public static IDisposable Refuse(Guid product)
    {
        Refusing[product] = true;
        return new Healing(product);
    }

    public async Task<Result> Handle(ProductCreatedV1 integrationEvent, CancellationToken cancellationToken)
    {
        var (connection, transaction) = unitOfWork.Join();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "insert into tests.product_audits (product_id) values (@ProductId)",
                new { integrationEvent.ProductId },
                transaction,
                cancellationToken: cancellationToken
            )
        );
        return Refusing.ContainsKey(integrationEvent.ProductId) ? Refused : Result.Success();
    }

    private sealed class Healing(Guid product) : IDisposable
    {
        public void Dispose() => Refusing.TryRemove(product, out _);
    }
}
