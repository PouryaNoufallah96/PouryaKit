using PouryaKit.Domain;

namespace PouryaKit.Email;

/// <summary>
/// Sends an email, part of the Email Capability; a provider package, such as <c>PouryaKit.Email.MailKit</c>, registers
/// the one implementation of a Host. Send from a Notification Handler, which runs after the business Operation commits,
/// so a rolled-back Operation sends nothing. A send is not transactional: when the delivery that sent a mail fails
/// afterwards, its retry sends the mail again.
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Sends <paramref name="message"/>, and succeeds once the mail server has accepted it. Fails with
    /// <see cref="EmailErrors.InvalidAddress"/>, sending nothing, when its recipient is not one mailbox address with a
    /// domain; throws when the mail server cannot be reached or refuses the mail.
    /// </summary>
    Task<Result> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
