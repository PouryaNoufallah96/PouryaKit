using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PouryaKit.Storage.S3;

// The Host's one IFileStorage, checked with the provider's options when the Host starts, over its final registrations,
// so that a provider registered before or after this one is found. It reads the registrations rather than resolving
// the providers: resolving would build each of them, and this provider's factory reads the options being validated.
internal sealed class OneFileStorage(IServiceCollection services) : IValidateOptions<PouryaKitS3StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, PouryaKitS3StorageOptions options)
    {
        List<string> providers =
        [
            .. services
                .Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IFileStorage))
                .Select(descriptor =>
                    (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType() ?? descriptor.ImplementationFactory!.Method.ReturnType).FullName!
                ),
        ];
        return providers.Count < 2
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"The Host registers {providers.Count} file storage providers, {string.Join(", ", providers)}, and composes exactly one IFileStorage: remove all but one of its registrations."
            );
    }
}
