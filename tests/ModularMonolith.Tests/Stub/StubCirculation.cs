using System.Collections.Concurrent;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

// One Stub Integration Event under two subscriptions, standing in for two Modules subscribed to one Integration Event,
// so either one can be made to fail without a deliberately broken handler in the Proving Sample. Each handler files its
// own copy of the note. Runs and faults are kept for the whole process, so a test sees them in every Host it starts.

internal sealed record NoteCirculatedV1(Guid Id, DateTimeOffset OccurredOn, string Text);

internal sealed record NoteToCirculateDomainEvent(string Text) : DomainEvent;

internal sealed class CirculateNoteNotificationHandler(IIntegrationEventPublisher events)
    : INotificationHandler<NoteToCirculateDomainEvent>
{
    public Task<Result> Handle(NoteToCirculateDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        events.Publish(domainEvent.Id, new NoteCirculatedV1(domainEvent.Id, domainEvent.OccurredOn, domainEvent.Text));
        return Task.FromResult(Result.Success());
    }
}

internal enum Failure
{
    FailedResult,
    Exception,
}

/// <summary>One run of a circulated note's handler, and the Operation its delivery ran as.</summary>
internal sealed record CirculationRun(string Subscription, string Text, Operation Operation);

internal static class Circulation
{
    public const string Ledger = "stub.circulated-ledger";
    public const string Archive = "stub.circulated-archive";

    public static readonly Error Refused = Error.Conflict("Note.CirculationRefused", "The circulated note was refused.");

    private static readonly ConcurrentQueue<CirculationRun> Runs = [];
    private static readonly ConcurrentDictionary<(string Subscription, string Text), (Failure Failure, int Runs)> Faults = new();

    /// <summary>Makes <paramref name="subscription"/>'s handler fail its first <paramref name="runs"/> runs of the note, or every run.</summary>
    public static void Fail(string subscription, string text, Failure failure = Failure.FailedResult, int runs = int.MaxValue) =>
        Faults[(subscription, text)] = (failure, runs);

    public static void Heal(string subscription, string text) => Faults.TryRemove((subscription, text), out _);

    public static IReadOnlyList<CirculationRun> RunsOf(string subscription, string text) =>
        [.. Runs.Where(run => run.Subscription == subscription && run.Text == text)];

    public static string CopyOf(string subscription, string text) => $"{subscription} copy of {text}";

    // The copy is written before the handler fails, so only a rollback removes it.
    public static async Task<Result> RunAsync(
        string subscription,
        NoteCirculatedV1 circulated,
        IRepository<Note, NoteId> notes,
        Operation operation,
        CancellationToken cancellationToken
    )
    {
        var earlier = RunsOf(subscription, circulated.Text).Count;
        Runs.Enqueue(new CirculationRun(subscription, circulated.Text, operation));
        await notes.AddAsync(Note.Write(CopyOf(subscription, circulated.Text)), cancellationToken);
        if (!Faults.TryGetValue((subscription, circulated.Text), out var fault) || earlier >= fault.Runs)
        {
            return Result.Success();
        }

        return fault.Failure == Failure.Exception
            ? throw new InvalidOperationException($"The {subscription} handler broke on {circulated.Text}.")
            : Refused;
    }
}

internal sealed class LedgerCirculatedNoteIntegrationEventHandler(IRepository<Note, NoteId> notes, Operation operation)
    : IIntegrationEventHandler<NoteCirculatedV1>
{
    public Task<Result> Handle(NoteCirculatedV1 integrationEvent, CancellationToken cancellationToken) =>
        Circulation.RunAsync(Circulation.Ledger, integrationEvent, notes, operation, cancellationToken);
}

internal sealed class ArchiveCirculatedNoteIntegrationEventHandler(IRepository<Note, NoteId> notes, Operation operation)
    : IIntegrationEventHandler<NoteCirculatedV1>
{
    public Task<Result> Handle(NoteCirculatedV1 integrationEvent, CancellationToken cancellationToken) =>
        Circulation.RunAsync(Circulation.Archive, integrationEvent, notes, operation, cancellationToken);
}
