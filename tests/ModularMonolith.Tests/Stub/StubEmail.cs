using System.Collections.Concurrent;
using PouryaKit.Domain;
using PouryaKit.Email;
using PouryaKit.Messaging;

namespace ModularMonolith.Tests.Stub;

// Notification Handlers that mail a note, raised by WriteNoteRaisingCommand: one sends, the other sends and then fails
// its first delivery, as a delivery that dies after the mail server accepted the mail.

#region mail-note-handler
internal sealed record NoteMailedDomainEvent(string To, string Text) : DomainEvent;

internal sealed class MailNoteNotificationHandler(IEmailSender email) : INotificationHandler<NoteMailedDomainEvent>
{
    public Task<Result> Handle(NoteMailedDomainEvent domainEvent, CancellationToken cancellationToken) =>
        email.SendAsync(new EmailMessage(domainEvent.To, domainEvent.Text, $"The note {domainEvent.Text} is written."), cancellationToken);
}
#endregion

internal sealed record NoteMailedThenFailingDomainEvent(string To, string Text) : DomainEvent;

internal sealed class MailNoteThenFailOnceNotificationHandler(IEmailSender email)
    : INotificationHandler<NoteMailedThenFailingDomainEvent>
{
    public static readonly Error FailedAfterSending = Error.Conflict(
        "Note.FailedAfterMailing",
        "The delivery failed after its mail was sent, once."
    );

    // Process-wide, so the Host that delivers counts whatever Host the test composed.
    private static readonly ConcurrentDictionary<string, int> Attempts = [];

    public async Task<Result> Handle(NoteMailedThenFailingDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var sent = await email.SendAsync(
            new EmailMessage(domainEvent.To, domainEvent.Text, $"The note {domainEvent.Text} is written."),
            cancellationToken
        );
        if (sent.IsFailure)
        {
            return sent;
        }

        return Attempts.AddOrUpdate(domainEvent.Text, 1, (_, attempts) => attempts + 1) == 1 ? FailedAfterSending : Result.Success();
    }
}
