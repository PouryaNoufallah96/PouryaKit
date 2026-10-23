using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PouryaKit.Cqrs;

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
        /// <c>Configure&lt;PouryaKitMailKitOptions&gt;(configuration.GetSection("PouryaKit:Email"))</c>;
        /// <paramref name="configure"/> sets them in code instead. The Host fails when it starts, naming the option, when
        /// <see cref="PouryaKitMailKitOptions.Host"/> is blank, the port is not one,
        /// <see cref="PouryaKitMailKitOptions.From"/> is not a mailbox address with a domain,
        /// <see cref="PouryaKitMailKitOptions.Security"/> is not a defined value, which MailKit would treat as plain SMTP,
        /// or only one of the user name and password is set. A Host composes exactly one <see cref="IEmailSender"/>: its
        /// build fails, through the Kit's composition checks, naming each registration's type when it registers another,
        /// before or after this call, a second call included, and naming the contract when it removes this one.
        /// </summary>
        public IServiceCollection AddPouryaKitMailKit(Action<PouryaKitMailKitOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);
            var options = services
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
            if (configure is not null)
            {
                options.Configure(configure);
            }

            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICompositionCheck, OneEmailSenderCheck>());
            services.AddSingleton<IEmailSender>(Create);
            return services;
        }
    }

    // A named method, which the one-provider check names.
    private static MailKitEmailSender Create(IServiceProvider provider) => new(provider.GetRequiredService<IOptions<PouryaKitMailKitOptions>>().Value);
}
