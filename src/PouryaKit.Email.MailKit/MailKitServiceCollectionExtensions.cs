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
        /// <see cref="PouryaKitMailKitOptions.From"/> is not a mailbox address with a domain, or only one of the user
        /// name and password is set. Call it once.
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
                    mail => string.IsNullOrEmpty(mail.UserName) == string.IsNullOrEmpty(mail.Password),
                    "PouryaKitMailKitOptions.UserName and Password are set together or not at all."
                )
                .ValidateOnStart();
            services.TryAddSingleton<IEmailSender>(provider =>
                new MailKitEmailSender(provider.GetRequiredService<IOptions<PouryaKitMailKitOptions>>().Value)
            );
            return services;
        }
    }
}
