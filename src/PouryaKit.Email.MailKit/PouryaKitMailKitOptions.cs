using MailKit.Security;

namespace PouryaKit.Email.MailKit;

/// <summary>
/// The SMTP server a Host sends its email through and the address it sends from, bound by the Host from its
/// configuration, such as the section <c>PouryaKit:Email</c>, and validated when the Host starts.
/// </summary>
public sealed class PouryaKitMailKitOptions
{
    /// <summary>The SMTP server's host name or address; required.</summary>
    public string? Host { get; set; }

    /// <summary>The SMTP server's port; by default 587, the submission port.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// How the connection is secured; by default <see cref="SecureSocketOptions.StartTls"/>, which refuses a server
    /// that does not offer TLS. Set <see cref="SecureSocketOptions.None"/> only for a local test server.
    /// </summary>
    public SecureSocketOptions Security { get; set; } = SecureSocketOptions.StartTls;

    /// <summary>The user name the Host authenticates with; null, with <see cref="Password"/>, to send unauthenticated.</summary>
    public string? UserName { get; set; }

    /// <summary>The password for <see cref="UserName"/>; set together with it.</summary>
    public string? Password { get; set; }

    /// <summary>The mailbox address every mail is sent from, such as <c>Shop &lt;shop@example.com&gt;</c>; required.</summary>
    public string? From { get; set; }
}
