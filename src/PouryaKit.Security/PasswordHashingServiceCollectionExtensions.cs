using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace PouryaKit.Security;

/// <summary>Composes password hashing, part of the Security Capability, on a Host's services.</summary>
public static class PasswordHashingServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="IPasswordHasher"/> as a singleton, replacing an earlier registration, built from the
        /// <see cref="PouryaKitPasswordHashingOptions"/>. <paramref name="configure"/> configures them as
        /// <c>Configure&lt;PouryaKitPasswordHashingOptions&gt;</c> does: each call adds to the options earlier calls and a
        /// bound configuration section set, and never restores the defaults. The Host fails to start, naming every
        /// problem, when an option is outside its documented bounds. Independent of <c>AddPouryaKitPermissions</c>.
        /// </summary>
        public IServiceCollection AddPouryaKitPasswordHashing(Action<PouryaKitPasswordHashingOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var options = services.AddOptions<PouryaKitPasswordHashingOptions>().ValidateOnStart();
            if (configure is not null)
            {
                options.Configure(configure);
            }

            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IValidateOptions<PouryaKitPasswordHashingOptions>, Pbkdf2PasswordHasher.Validation>()
            );
            services.Replace(
                ServiceDescriptor.Singleton<IPasswordHasher>(provider =>
                    Pbkdf2PasswordHasher.Create(provider.GetRequiredService<IOptions<PouryaKitPasswordHashingOptions>>().Value)
                )
            );
            return services;
        }
    }
}
