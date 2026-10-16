using M2.Catalog;
using M2.Ledger;
using M2.Marten;
using Marten;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging;
using Wolverine.EntityFrameworkCore;
using Wolverine.Marten;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;

namespace M2.Tests;

// Item 10: the store-neutral part of the Kit's EF outbox write, copied once, writes through Wolverine's EF outbox and
// through its Marten outbox alike: both are a MessageContext whose envelope transaction is enrolled in one store. The
// same Operation's write stores one envelope through each, routed and headed the same way.
public sealed class StoreNeutralOutboxTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task One_write_stores_the_same_routed_envelope_through_the_ef_and_the_marten_outbox()
    {
        var host = await StartAsync();
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        var context = Context("acme");
        var accountId = Guid.CreateVersion7();

        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.BeginOperation(context, typeof(CatalogModule));
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(Token);
            var outbox = new DbContextOutbox(runtime, []);
            outbox.Enroll(dbContext);
            await new StoreNeutralOutbox(runtime, scope.ServiceProvider).WriteAsync(outbox, [new Deposited(accountId, 1)], Token);
            await dbContext.SaveChangesAsync(Token);
            await transaction.CommitAsync(Token);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.BeginOperation(context, typeof(LedgerModule));
            await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession("acme");
            await new StoreNeutralOutbox(runtime, scope.ServiceProvider).WriteAsync(new MartenOutbox(runtime, session), [new Deposited(accountId, 2)], Token);
            await session.SaveChangesAsync(Token);
        }

        var envelopes = (await Db.RowsAsync(ConnectionString, "select body from wolverine.wolverine_incoming_envelopes where received_at = 'local://ledger.note-deposit/'"))
            .Select(row => EnvelopeSerializer.Deserialize((byte[])row[0]!))
            .ToList();
        Assert.Equal(2, envelopes.Count);
        Assert.Equal(envelopes[0].MessageType, envelopes[1].MessageType);
        string[] contextHeaders = [OperationContextHeaders.ActorId, OperationContextHeaders.ActorKind, OperationContextHeaders.Tenant, OperationContextHeaders.CorrelationId, OperationContextHeaders.CausationId];
        Assert.All(contextHeaders, header => Assert.Equal(envelopes[0].Headers[header], envelopes[1].Headers[header]));
        Assert.Equal("acme", envelopes[0].Headers[OperationContextHeaders.Tenant]);
    }
}
