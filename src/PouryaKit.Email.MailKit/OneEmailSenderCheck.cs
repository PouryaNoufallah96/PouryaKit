using PouryaKit.Cqrs;

namespace PouryaKit.Email.MailKit;

// A Host composes exactly one IEmailSender, which its final registrations show whatever their order: with two, the
// container would serve the last silently; with none, a removal after the provider's registration, a Notification Handler
// would fail only at resolution, or at build for a constructor-injected consumer. One problem for any other count, naming each registration's type, or
// the factory that builds it.
internal sealed class OneEmailSenderCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        // A keyed descriptor's ImplementationType throws, and a Notification Handler resolves IEmailSender unkeyed.
        List<string> registered =
        [
            .. composition
                .Registrations.Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IEmailSender))
                .Select(descriptor =>
                    (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType())?.FullName
                    ?? $"the factory {descriptor.ImplementationFactory!.Method.DeclaringType?.FullName}.{descriptor.ImplementationFactory.Method.Name}"
                ),
        ];
        return registered.Count switch
        {
            1 => [],
            0 => ["The Host registers no IEmailSender provider, and composes exactly one: AddPouryaKitMailKit registered one, then it was removed."],
            _ =>
            [
                $"The Host registers {registered.Count} IEmailSender providers, {string.Join(", ", registered)}, and composes exactly one: remove all but one of its email sender registrations.",
            ],
        };
    }
}
