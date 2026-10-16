using PouryaKit.Domain;

namespace PouryaKit.Email;

/// <summary>The Errors an <see cref="IEmailSender"/> fails a send with.</summary>
public static class EmailErrors
{
    /// <summary>The message's recipient is not one mailbox address with a domain. Nothing was sent.</summary>
    public static readonly Error InvalidAddress = Error.Validation(
        "Email.InvalidAddress",
        "The recipient is not one mailbox address with a domain, such as ada@example.com.",
        nameof(EmailMessage.To)
    );
}
