using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Email.MailKit;

/// <summary>Composes the Email Capability's MailKit sender on a Host's services.</summary>
public static class MailKitServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the Host's one <see cref="IEmailSender"/>, which sends each mail through the SMTP server of
        /// <see cref="PouryaKitMailKitOptions"/> on a connection of its own: it connects, authenticates when a user name
        /// is set, sends and disconnects. The Host binds the options from its configuration, as with
        /// <c>Configure&lt;PouryaKitMailKitOptions&gt;(configuration.GetSection("PouryaKit:Email"))</c>, and fails when it
        /// starts, naming the option, when <see cref="PouryaKitMailKitOptions.Host"/> is blank, the port is not one,
        /// <see cref="PouryaKitMailKitOptions.From"/> is not a mailbox address with a domain,
        /// <see cref="PouryaKitMailKitOptions.Security"/> is not a defined value, which MailKit would treat as plain SMTP,
        /// or only one of the user name and password is set, and when it registers another <see cref="IEmailSender"/>,
        /// before or after this call, naming each implementation: a Host composes exactly one.
        /// </summary>
        public IServiceCollection AddPouryaKitMailKit()
        {
            ArgumentNullException.ThrowIfNull(services);
            services
                .AddOptions<PouryaKitMailKitOptions>()
                .Validate(
                    mail => !string.IsNullOrWhiteSpace(mail.Host),
                    "PouryaKitMailKitOptions.Host names no SMTP server; set the host name of the server the Host sends its email through."
                )
                .Validate(
                    mail => mail.Port is > 0 and <= 65535,
                    "PouryaKitMailKitOptions.Port is not a TCP port; set it between 1 and 65535."
                )
                .Validate(
                    mail => MailKitEmailSender.TryParseMailbox(mail.From, out _),
                    "PouryaKitMailKitOptions.From is not a mailbox address with a domain; set the address the Host's email is sent from."
                )
                .Validate(
                    mail => Enum.IsDefined(mail.Security),
                    "PouryaKitMailKitOptions.Security is not a SecureSocketOptions value; set None, Auto, SslOnConnect, StartTls or StartTlsWhenAvailable."
                )
                .Validate(
                    mail => string.IsNullOrEmpty(mail.UserName) == string.IsNullOrEmpty(mail.Password),
                    "PouryaKitMailKitOptions.UserName and Password are set together or not at all."
                )
                .ValidateOnStart();
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PouryaKitMailKitOptions>>(new OneEmailSender(services)));
            services.AddSingleton<IEmailSender>(Create);
            return services;
        }
    }

    // Typed as the implementation, so that the one-provider check names it.
    private static MailKitEmailSender Create(IServiceProvider provider) => new(provider.GetRequiredService<IOptions<PouryaKitMailKitOptions>>().Value);
}
