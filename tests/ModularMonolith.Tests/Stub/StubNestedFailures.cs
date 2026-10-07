using FluentValidation;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence;

namespace ModularMonolith.Tests.Stub;

public enum NestedFailure
{
    None,
    Handler,
    Validation,
    Authorization,
}

// Posts a note whose Domain Event a Notification Handler subscribes to, so the Operation stores a Notification when it
// commits; then sends a nested command that fails as asked, ignores its Result, and returns success. It opts in to
// idempotency, which a request id turns on; sent without one, it runs every time.
[Idempotent]
internal sealed record PostNoteThenNestCommand(string Text, NestedFailure Failure) : Command;

internal sealed class PostNoteThenNestCommandHandler(IRepository<Note, NoteId> notes, IDispatcher dispatcher)
    : CommandHandler<PostNoteThenNestCommand>
{
    public override async Task<Result> Handle(PostNoteThenNestCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write(command.Text).Raising(new NotePostedDomainEvent(command.Text)), cancellationToken);
        _ = await dispatcher.SendAsync(new NestedNoteCommand(command.Text, command.Failure), cancellationToken); // ignored on purpose
        return Result.Success();
    }
}

internal sealed record NestedNoteCommand(string Text, NestedFailure Failure) : Command;

internal sealed class NestedNoteCommandHandler(IRepository<Note, NoteId> notes) : CommandHandler<NestedNoteCommand>
{
    public static readonly Error Refused = Error.Conflict("Note.NestedRefused", "The nested note was refused.");

    public override async Task<Result> Handle(NestedNoteCommand command, CancellationToken cancellationToken)
    {
        await notes.AddAsync(Note.Write($"nested {command.Text}"), cancellationToken);
        return command.Failure == NestedFailure.Handler ? Refused : Result.Success();
    }
}

internal sealed class NestedNoteCommandValidator : AbstractValidator<NestedNoteCommand>
{
    public NestedNoteCommandValidator() => RuleFor(command => command.Failure).NotEqual(NestedFailure.Validation);
}

internal sealed class NestedNoteCommandAuthorizer : IAuthorizer<NestedNoteCommand>
{
    public static readonly Error Forbidden = Error.Forbidden("Note.NestedForbidden", "The nested note is not allowed.");

    public Task<Result> AuthorizeAsync(NestedNoteCommand operation, CancellationToken cancellationToken) =>
        Task.FromResult(operation.Failure == NestedFailure.Authorization ? (Result)Forbidden : Result.Success());
}
