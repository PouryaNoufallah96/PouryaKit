using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PouryaKit.Email.MailKit;

// The Host's one IEmailSender, checked with the provider's options when the Host starts, over its final registrations,
// so that a provider registered before or after this one is found. It reads the registrations rather than resolving
// the providers: resolving would build each of them, and this provider's factory reads the options being validated.
internal sealed class OneEmailSender(IServiceCollection services) : IValidateOptions<PouryaKitMailKitOptions>
{
    public ValidateOptionsResult Validate(string? name, PouryaKitMailKitOptions options)
    {
        List<string> providers =
        [
            .. services
                .Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IEmailSender))
                .Select(descriptor =>
                    (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType() ?? descriptor.ImplementationFactory!.Method.ReturnType).FullName!
                ),
        ];
        return providers.Count < 2
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"The Host registers {providers.Count} email senders, {string.Join(", ", providers)}, and composes exactly one IEmailSender: remove all but one of its registrations."
            );
    }
}
