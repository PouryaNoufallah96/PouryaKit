using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

// Domain Event Handlers that the Proving Sample must not hold: a cycle, a refusal followed by a reaction that must
// not run, and a handler that ignores its nested command's failure.

internal enum Reaction
{
    Chain,
    Cycle,
    Refuse,
    IgnoreNestedFailure,
    None,
}

internal sealed record WriteReactingNoteCommand(Reaction Reaction) : Command;

internal sealed class WriteReactingNoteCommandHandler(IRepository<Note, NoteId> notes)
    : CommandHandler<WriteReactingNoteCommand>
{
    public override async Task<Result> Handle(
        WriteReactingNoteCommand command,
        CancellationToken cancellationToken
    )
    {
        DomainEvent raised = command.Reaction switch
        {
            Reaction.Chain => new NoteLinkedDomainEvent(1),
            Reaction.Cycle => new NotePingedDomainEvent(),
            Reaction.Refuse => new NoteRefusedDomainEvent(),
            Reaction.IgnoreNestedFailure => new NoteForwardedDomainEvent(),
            Reaction.None => new NoteFiledDomainEvent(),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var note = Note.Write($"{command.Reaction}").Raising(raised);
        if (command.Reaction == Reaction.IgnoreNestedFailure)
        {
            // A Notification Handler subscribes to this one, so a commit would store a Notification in the Outbox.
            note.Raising(new NotePostedDomainEvent($"{command.Reaction}"));
        }

        await notes.AddAsync(note, cancellationToken);
        return Result.Success();
    }
}

// Nothing handles this one.
internal sealed record NoteFiledDomainEvent : DomainEvent;

// A chain exactly as deep as the drain allows: link n is written in round n and raises link n + 1.
internal sealed record NoteLinkedDomainEvent(int Link) : DomainEvent;

internal sealed class LinkNoteReaction(IRepository<Note, NoteId> notes)
    : IDomainEventHandler<NoteLinkedDomainEvent>
{
    public async Task<Result> Handle(
        NoteLinkedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        var note = Note.Write($"link {domainEvent.Link}");
        if (domainEvent.Link < DomainEventDrain.MaxDepth)
        {
            note.Raising(new NoteLinkedDomainEvent(domainEvent.Link + 1));
        }

        await notes.AddAsync(note, cancellationToken);
        return Result.Success();
    }
}

internal sealed record NotePingedDomainEvent : DomainEvent;

internal sealed record NotePongedDomainEvent : DomainEvent;

internal sealed class PingReaction(IRepository<Note, NoteId> notes)
    : IDomainEventHandler<NotePingedDomainEvent>
{
    public async Task<Result> Handle(
        NotePingedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        await notes.AddAsync(
            Note.Write("pong").Raising(new NotePongedDomainEvent()),
            cancellationToken
        );
        return Result.Success();
    }
}

internal sealed class PongReaction(IRepository<Note, NoteId> notes)
    : IDomainEventHandler<NotePongedDomainEvent>
{
    public async Task<Result> Handle(
        NotePongedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        // A real wait: without the depth limit the cycle would run until the test's timeout cancels it.
        await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
        await notes.AddAsync(
            Note.Write("ping").Raising(new NotePingedDomainEvent()),
            cancellationToken
        );
        return Result.Success();
    }
}

// Three handlers of one Domain Event, run in the order of their names: the second refuses.
internal sealed record NoteRefusedDomainEvent : DomainEvent;

// Writes straight to the database inside the Operation's transaction, like WriteNoteThenFailCommandHandler, so only a
// rollback can remove its row.
internal sealed class RefusalFirstReaction(StubDbContext dbContext)
    : IDomainEventHandler<NoteRefusedDomainEvent>
{
    public async Task<Result> Handle(
        NoteRefusedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"insert into stub.notes (text, version) values ('first reaction', 0)",
            cancellationToken
        );
        return Result.Success();
    }
}

internal sealed class RefusalSecondReaction : IDomainEventHandler<NoteRefusedDomainEvent>
{
    public static readonly Error Refused = Error.Conflict(
        "Note.Refused",
        "The second reaction refused the note."
    );

    public Task<Result> Handle(
        NoteRefusedDomainEvent domainEvent,
        CancellationToken cancellationToken
    ) => Task.FromResult<Result>(Refused);
}

internal sealed class RefusalThirdReaction : IDomainEventHandler<NoteRefusedDomainEvent>
{
    public Task<Result> Handle(
        NoteRefusedDomainEvent domainEvent,
        CancellationToken cancellationToken
    ) => throw new InvalidOperationException("A reaction ran after an earlier one refused.");
}

internal sealed record NoteForwardedDomainEvent : DomainEvent;

internal sealed class ForwardingReaction(IDispatcher dispatcher)
    : IDomainEventHandler<NoteForwardedDomainEvent>
{
    public async Task<Result> Handle(
        NoteForwardedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        _ = await dispatcher.SendAsync(
            new WriteNoteThenFailCommand("nested", Ending.Fail),
            cancellationToken
        ); // ignored on purpose
        return Result.Success();
    }
}

// Three handlers of one Domain Event that record when each starts and ends. Declared out of name order, so the
// order they run in comes from the drain and not from the order they were registered in.
internal sealed record NoteSharedDomainEvent : DomainEvent;

internal sealed record WriteSharedNoteCommand : Command<ReactionLog>;

internal sealed class WriteSharedNoteCommandHandler(
    IRepository<Note, NoteId> notes,
    ReactionLog log
) : CommandHandler<WriteSharedNoteCommand, ReactionLog>
{
    // The log is the Operation's own; returning it lets the test read what the drain did after the handler returned.
    public override async Task<Result<ReactionLog>> Handle(
        WriteSharedNoteCommand command,
        CancellationToken cancellationToken
    )
    {
        await notes.AddAsync(
            Note.Write("shared").Raising(new NoteSharedDomainEvent()),
            cancellationToken
        );
        return log;
    }
}

internal sealed class ReactionLog
{
    private readonly Lock _lock = new();
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public void Add(string entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }
}

internal abstract class SharedNoteReaction(ReactionLog log)
    : IDomainEventHandler<NoteSharedDomainEvent>
{
    public async Task<Result> Handle(
        NoteSharedDomainEvent domainEvent,
        CancellationToken cancellationToken
    )
    {
        log.Add($"{GetType().Name} started");
        await Task.Yield();
        log.Add($"{GetType().Name} ended");
        return Result.Success();
    }
}

internal sealed class ZuluReaction(ReactionLog log) : SharedNoteReaction(log);

internal sealed class AlphaReaction(ReactionLog log) : SharedNoteReaction(log);

internal sealed class MikeReaction(ReactionLog log) : SharedNoteReaction(log);
