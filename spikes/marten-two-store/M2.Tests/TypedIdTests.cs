using System.Text.Json;
using M2.Ledger;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Domain;

namespace M2.Tests;

public sealed record NoteId(Guid Value) : TypedId<Guid>(Value);

public sealed class Note
{
    public NoteId? Id { get; set; }
}

// Item 5: Marten's serializer takes the Kit's JSON (UsePouryaKitJson), so a typed ID in an event is its bare value, as
// in an HTTP body or an envelope. A Kit typed ID is a class; Marten takes only an immutable struct as a strong-typed
// identity.
public sealed class TypedIdTests(Postgres postgres) : SpikeTest(postgres)
{
    [Fact]
    public async Task A_typed_id_in_an_event_is_stored_as_the_kits_json_writes_it_and_folds_back()
    {
        var accountId = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();
        var host = await StartAsync();

        await Ledger(host).SendAsync(new OpenAccountCommand(accountId, customer), Context(), Token);

        var stored = await Db.ScalarAsync<string>(ConnectionString, "select (data -> 'customer')::text from events.mt_events where stream_id = $1 and type = 'account_opened'", accountId);
        Assert.Equal(JsonSerializer.Serialize(new CustomerId(customer), new JsonSerializerOptions().UsePouryaKitJson()), stored);
        var account = await Ledger(host).QueryAsync(new GetAccountQuery(accountId), Context(), Token);
        Assert.Equal(customer, account.Value.Customer);
    }

    [Fact]
    public async Task A_kit_typed_id_is_not_taken_as_a_marten_document_identity()
    {
        var host = await StartAsync();
        await using var session = host.Services.GetRequiredService<IDocumentStore>().LightweightSession();

        var refused = await Record.ExceptionAsync(async () =>
        {
            session.Store(new Note { Id = new NoteId(Guid.CreateVersion7()) });
            await session.SaveChangesAsync(Token);
        });

        Assert.Contains("No closed-shape id strategy is registered for M2.Tests.Note", Assert.IsType<InvalidDocumentException>(refused).Message, StringComparison.Ordinal);
    }
}
