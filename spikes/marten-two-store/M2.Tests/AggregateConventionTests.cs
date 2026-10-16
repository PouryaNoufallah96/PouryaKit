using JasperFx.Events.Projections;
using M2.Ledger;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace M2.Tests;

// Marten 9 folds a Stream through the dispatcher JasperFx.Events' source generator writes at compile time, and the
// generator takes only public Apply, Create and ShouldDelete methods; the type itself may be internal. Plan I.4 read the
// Marten docs as allowing internal Apply methods; on 9.46.0 they are declined.
public sealed class AggregateConventionTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_stream_with_internal_apply_methods_has_no_generated_dispatcher()
    {
        var host = await StartAsync();
        await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        var refused = await Assert.ThrowsAsync<InvalidProjectionException>(() => session.Events.FetchForWriting<Dormant>(Guid.CreateVersion7(), Token));

        Assert.Contains("No source-generated dispatcher found", refused.Message, StringComparison.Ordinal);
    }
}
