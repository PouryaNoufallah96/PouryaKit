using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Domain;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Testing;
using Wolverine.Runtime;

namespace ModularMonolith.Tests;

/// <summary>
/// A delivery that keeps failing is retried, then set aside as a dead letter of its own subscription for inspection and
/// replay; it is never treated as handled, and the other subscription's delivery is not touched.
/// </summary>
public sealed class FailingDeliveryTests(AppFixture app) : HostTest<AppFixture>(app)
{
    private const string WireName = "stub.note-circulated.v1";

    [Theory]
    [InlineData(nameof(Failure.FailedResult), typeof(IntegrationEventHandlerFailedException))]
    [InlineData(nameof(Failure.Exception), typeof(InvalidOperationException))]
    public async Task A_failed_result_is_retried_like_an_exception_each_attempt_in_a_new_scope(string failure, Type lastFailure)
    {
        var text = Unique();
        Circulation.Fail(Circulation.Archive, text, Enum.Parse<Failure>(failure));

        await Fixture.WaitForDeliveriesAsync(() => CirculateAsync(new NoteToCirculateDomainEvent(text)), TestContext.Current.CancellationToken);

        var runs = Circulation.RunsOf(Circulation.Archive, text);
        Assert.Equal(1 + TestDeliveryServiceCollectionExtensions.RetryDelays.Count, runs.Count);
        Assert.Equal(runs.Count, runs.Select(run => run.Operation).Distinct().Count());
        Assert.Equal(lastFailure.FullName, Assert.Single(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken)).ExceptionType);
        // Each attempt rolled back, so none of them left its copy behind.
        Assert.Equal(0L, await CopiesAsync(Circulation.Archive, text));
    }

    [Fact]
    public async Task Exhausted_retries_set_only_that_subscriptions_delivery_aside_as_a_dead_letter_never_handled()
    {
        var text = Unique();
        Circulation.Fail(Circulation.Archive, text);

        await Fixture.WaitForDeliveriesAsync(() => CirculateAsync(new NoteToCirculateDomainEvent(text)), TestContext.Current.CancellationToken);

        // Found by its subscription, with the Errors of the failed Result that set it aside.
        var deadLetter = Assert.Single(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken), deadLetter => deadLetter.Subscription == Circulation.Archive);
        Assert.Equal((WireName, typeof(IntegrationEventHandlerFailedException).FullName), (deadLetter.WireName, deadLetter.ExceptionType));
        Assert.Contains($"{Circulation.Refused.Code} ({Circulation.Refused.Message})", deadLetter.ExceptionMessage);
        // Its envelope left the Inbox for dead-letter storage instead of being marked handled; the other is handled.
        Assert.Equal(
            [[$"local://{Circulation.Ledger}/", "Handled"]],
            await Fixture.RowsAsync($"select received_at, status from wolverine.wolverine_incoming_envelopes where message_type = '{WireName}'")
        );
        Assert.Equal(1L, await CopiesAsync(Circulation.Ledger, text));
    }

    [Fact]
    public async Task A_replayed_dead_letter_runs_only_its_subscription_once_and_its_receipt_makes_a_second_replay_apply_nothing()
    {
        var text = Unique();
        var circulated = new NoteToCirculateDomainEvent(text);
        Circulation.Fail(Circulation.Archive, text);
        await Fixture.WaitForDeliveriesAsync(() => CirculateAsync(circulated), TestContext.Current.CancellationToken);
        var deadLetter = Assert.Single(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
        var store = Fixture.Host.Services.GetRequiredService<IWolverineRuntime>().Storage;
        var setAside = (await store.DeadLetters.DeadLetterEnvelopeByIdAsync(deadLetter.Id))!.Envelope;
        var failedRuns = Circulation.RunsOf(Circulation.Archive, text).Count;

        // The cause is fixed, then the dead letter is replayed.
        Circulation.Heal(Circulation.Archive, text);
        await Fixture.ReplayAsync(deadLetter, TestContext.Current.CancellationToken);

        Assert.Equal(failedRuns + 1, Circulation.RunsOf(Circulation.Archive, text).Count);
        Assert.Single(Circulation.RunsOf(Circulation.Ledger, text));
        Assert.Empty(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
        Assert.Equal((1L, 1L), (await CopiesAsync(Circulation.Archive, text), await CopiesAsync(Circulation.Ledger, text)));

        // The same delivery set aside once more, as Wolverine sets one aside, and replayed again: it is handed out, and
        // its Receipt stops the handler.
        await store.Inbox.MoveToDeadLetterStorageAsync(setAside, new InvalidOperationException("Set aside once more."));
        await Fixture.ReplayAsync(Assert.Single(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);

        Assert.Equal(failedRuns + 2, DeliveryProbes.AttemptsOf(Circulation.Archive, circulated.Id).Count);
        Assert.Equal(failedRuns + 1, Circulation.RunsOf(Circulation.Archive, text).Count);
        Assert.Equal(1L, await CopiesAsync(Circulation.Archive, text));
        Assert.Equal(
            "Handled",
            await Fixture.ScalarAsync<string>($"select status from wolverine.wolverine_incoming_envelopes where received_at = 'local://{Circulation.Archive}/'")
        );
    }

    [Fact]
    public async Task Replaying_what_is_not_a_dead_letter_fails()
    {
        var thrown = await Assert.ThrowsAsync<DeliveryAssertionException>(() =>
            Fixture.ReplayAsync(
                new DeadLetter(Guid.CreateVersion7(), Circulation.Archive, WireName, "", ""),
                TestContext.Current.CancellationToken
            )
        );

        Assert.EndsWith("is not a dead letter, so there is nothing to replay.", thrown.Message);
    }

    [Fact]
    public async Task A_stored_delivery_to_a_removed_subscription_is_dead_lettered_while_the_remaining_subscription_handles_its_own()
    {
        var text = Unique();
        var circulated = new NoteToCirculateDomainEvent(text);
        await Fixture.WaitForDeliveriesAsync(() => CirculateAsync(circulated), TestContext.Current.CancellationToken);

        // Both deliveries stored and pending again, the ledger's at the queue of a subscription this Host no longer
        // declares, as after a release that removed it while its Integration Event keeps the archive's subscription.
        Assert.Equal(
            2,
            (
                await Fixture.RowsAsync(
                    $"update wolverine.wolverine_incoming_envelopes set status = 'Incoming', owner_id = 0, received_at = replace(received_at, '{Circulation.Ledger}', 'stub.circulated-retired') where message_type = '{WireName}' returning id"
                )
            ).Count
        );
        await Fixture.WaitUntilDeliveredAsync(TestContext.Current.CancellationToken);

        var deadLetter = Assert.Single(await Fixture.DeadLettersAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("stub.circulated-retired", typeof(UnknownSubscriptionException).FullName), (deadLetter.Subscription, deadLetter.ExceptionType));
        Assert.Equal(
            [[$"local://{Circulation.Archive}/", "Handled"]],
            await Fixture.RowsAsync($"select received_at, status from wolverine.wolverine_incoming_envelopes where message_type = '{WireName}'")
        );
        Assert.Equal(2, DeliveryProbes.AttemptsOf(Circulation.Archive, circulated.Id).Count);
        Assert.Equal(1L, await CopiesAsync(Circulation.Archive, text));
    }

    private Task<Result> CirculateAsync(NoteToCirculateDomainEvent circulated) =>
        Fixture
            .Gateway<StubModule>()
            .SendAsync(new WriteNoteRaisingCommand(circulated.Text, [circulated]), TestContext.Current.CancellationToken);

    private Task<long> CopiesAsync(string subscription, string text) =>
        Fixture.ScalarAsync<long>($"select count(*) from stub.notes where text = '{Circulation.CopyOf(subscription, text)}'");

    private static string Unique() => $"note {Guid.CreateVersion7()}";
}
