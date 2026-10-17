namespace PouryaKit.Email;

/// <summary>
/// One plain-text email to one recipient, <paramref name="To"/>, such as <c>ada@example.com</c> or
/// <c>Ada &lt;ada@example.com&gt;</c>; the sender is the address the Host configures for its provider.
/// </summary>
/// <param name="To">The recipient's mailbox address.</param>
/// <param name="Subject">The subject line.</param>
/// <param name="Body">The body, as plain text.</param>
public sealed record EmailMessage(string To, string Subject, string Body);
