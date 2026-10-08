using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Messaging;
using PouryaKit.Testing;
using Wolverine;

namespace ModularMonolith.Tests;

// The strict wire options write as they read (#60): an Integration Event a Notification Handler publishes with null in a
// member its contract forbids cannot be written to the Outbox, which its Unit of Work writes before the save, so the
// delivery fails and nothing it did commits: its effect, its Receipt, its publication record, the envelope and every
// receiver's effect (the final structural review of Wave 2, finding 7). With the text, the same delivery commits each.
public sealed class StrictWireWritingTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string Receiving = "stub.note-shared";

    private static readonly string Publishing = MessageNames.SubscriptionNameOf(
        typeof(CopyAndShareNoteNotificationHandler),
        typeof(NoteToCopyAndShareDomainEvent)
    );

    [Theory]
    [InlineData("without its text", false)]
    [InlineData("with its text", true)]
    public async Task A_notification_handlers_integration_event_commits_with_its_effect_only_when_the_wire_options_can_write_it(
        string payload,
        bool writable
    )
    {
        var text = $"note {Guid.CreateVersion7()}";
        var raised = new NoteToCopyAndShareDomainEvent(text, WithoutText: !writable);

        var (written, deliveries) = await Fixture.WaitForDeliveriesAsync(
            () => Fixture.Gateway<StubModule>().SendAsync(new WriteNoteRaisingCommand(text, [raised]), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken
        );

        Assert.True(written.IsSuccess, payload);
        var published = new Delivery(Publishing, MessageNames.WireNameOf(typeof(NoteToCopyAndShareDomainEvent)), DeadLettered: !writable);
        Assert.Equal(
            writable ? [published, new Delivery(Receiving, "stub.note-shared.v1", false)] : [published],
            deliveries.OrderBy(delivery => delivery.Subscription, StringComparer.Ordinal)
        );
        var committed = writable ? 1L : 0L;
        Assert.Equal(
            [committed, committed, committed, committed, committed],
            [
                await Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{CopyFiledNoteNotificationHandler.CopyOf(text)}'"),
                await Fixture.ScalarAsync<long>($"select count(*) from stub.pouryakit_receipts where subscription = '{Publishing}' and message_id = '{raised.Id}'"),
                await Fixture.ScalarAsync<long>($"select count(*) from stub.pouryakit_receipts where subscription = 'stub.note-shared.v1#published' and message_id = '{raised.Id}'"),
                await Fixture.ScalarAsync<long>($"select count(*) from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Receiving}/'"),
                await Fixture.ScalarAsync<long>($"select count(*) from stub.pouryakit_receipts where subscription = '{Receiving}' and message_id = '{raised.Id}'"),
            ]
        );
        Assert.Equal(
            (int)committed,
            Fixture.Host.Services.GetRequiredService<IntegrationEventLog>().All.Count(received => received.IntegrationEvent is NoteSharedV1 shared && shared.Id == raised.Id)
        );
        var deadLetters = await Fixture.RowsAsync("select received_at, exception_type, exception_message from wolverine.wolverine_dead_letters");
        if (writable)
        {
            Assert.Empty(deadLetters);
        }
        else
        {
            // Wolverine wraps the JsonException that writing the envelope's body threw.
            var deadLetter = Assert.Single(deadLetters);
            Assert.Equal([$"local://{Publishing}/", typeof(WolverineSerializationException).FullName!], deadLetter[..2]);
            Assert.StartsWith($"Error trying to serialize message of type {typeof(NoteSharedV1).FullName}", (string)deadLetter[2], StringComparison.Ordinal);
            Assert.Contains(
                Fixture.Logs.Entries,
                entry => entry.Exception is WolverineSerializationException { InnerException: JsonException inner } && inner.Message.Contains($"'{typeof(NoteSharedV1).FullName}' doesn't allow getting null values", StringComparison.Ordinal)
            );
        }
    }
}
