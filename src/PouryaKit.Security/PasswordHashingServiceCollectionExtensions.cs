using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PouryaKit.Security;

public static class PasswordHashingServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers <see cref="IPasswordHasher"/> as a singleton, replacing an earlier registration, with the
        /// <see cref="PasswordHashingOptions"/> <paramref name="configure"/> sets. Throws, naming every problem, when an
        /// option is outside its documented bounds. Independent of <c>AddPouryaKitPermissions</c>.
        /// </summary>
        public IServiceCollection AddPouryaKitPasswordHashing(Action<PasswordHashingOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(services);

            var options = new PasswordHashingOptions();
            configure?.Invoke(options);
            services.Replace(ServiceDescriptor.Singleton<IPasswordHasher>(Pbkdf2PasswordHasher.Create(options)));
            return services;
        }
    }
}
