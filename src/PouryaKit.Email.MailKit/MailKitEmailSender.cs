using System.Diagnostics.CodeAnalysis;
using MailKit.Net.Smtp;
using MimeKit;
using MimeKit.Text;
using PouryaKit.Domain;

namespace PouryaKit.Email.MailKit;

// One registration, a connection per send: MailKit's SmtpClient is not shared between threads without a lock, and a
// connection held between the sporadic sends of Notification deliveries would be dropped by the server when idle, so
// each send connects, authenticates, sends and disconnects on a client of its own, and concurrent deliveries never wait
// for each other. A failure after the server accepted the mail, such as the delivery's own failure, is retried by the
// delivery, which sends the mail again.
// Its options were validated when the Host started, so Host and From are set.
internal sealed class MailKitEmailSender(PouryaKitMailKitOptions options) : IEmailSender
{
    private readonly MailboxAddress _from = MailboxAddress.Parse(options.From!);

    public async Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!TryParseMailbox(message.To, out var to))
        {
            return EmailErrors.InvalidAddress;
        }

        using var mail = new MimeMessage([_from], [to], message.Subject, new TextPart(TextFormat.Plain) { Text = message.Body });
        using var client = new SmtpClient();
        await client.ConnectAsync(options.Host!, options.Port, options.Security, cancellationToken);
        if (options is { UserName: { Length: > 0 } userName, Password: { } password })
        {
            await client.AuthenticateAsync(userName, password, cancellationToken);
        }

        await client.SendAsync(mail, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return Result.Success();
    }

    // MimeKit also reads a bare local part, such as "ada", as a mailbox; a mail needs a domain to be delivered.
    internal static bool TryParseMailbox(string? text, [NotNullWhen(true)] out MailboxAddress? mailbox) =>
        MailboxAddress.TryParse(text, out mailbox) && mailbox.Domain.Length > 0;
}
