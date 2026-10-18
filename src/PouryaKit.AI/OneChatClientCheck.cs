using Microsoft.Extensions.AI;
using PouryaKit.Cqrs;

namespace PouryaKit.AI;

// A Host composes exactly one IChatClient, which its final registrations show whatever their order: with two, the
// container would serve the last silently, without the allow-list and the iteration cap of the Kit's; with none, a
// removal after the Kit's registration, a run would fail only at resolution. One problem for any other count, naming each
// registration's type, or the factory that builds it.
internal sealed class OneChatClientCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        // A keyed descriptor's ImplementationType is not its type, and a run resolves IChatClient unkeyed.
        List<string> registered =
        [
            .. composition
                .Registrations.Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IChatClient))
                .Select(descriptor =>
                    (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType())?.FullName
                    ?? $"the factory {descriptor.ImplementationFactory!.Method.DeclaringType?.FullName}.{descriptor.ImplementationFactory.Method.Name}"
                ),
        ];
        return registered.Count switch
        {
            1 => [],
            0 => ["The Host registers no IChatClient, and composes exactly one: AddPouryaKitAI registered one, then it was removed."],
            _ =>
            [
                $"The Host registers {registered.Count} IChatClient providers, {string.Join(", ", registered)}, and composes exactly one: remove all but one of its chat client registrations.",
            ],
        };
    }
}
