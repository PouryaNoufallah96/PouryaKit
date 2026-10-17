using PouryaKit.Cqrs;

namespace PouryaKit.Storage;

// A Host composes exactly one IFileStorage, which its final registrations show whatever their order: with two, the
// container would serve the last silently; with none, a removal after the provider's registration, a Feature would fail
// only at resolution, or at build for a constructor-injected consumer. One problem for any other count, naming each
// registration's type, or the factory that builds it.
internal sealed class OneFileStorageCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        // A keyed descriptor's ImplementationType throws, and Feature code resolves IFileStorage unkeyed.
        List<string> registered =
        [
            .. composition
                .Registrations.Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IFileStorage))
                .Select(descriptor =>
                    (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType())?.FullName
                    ?? $"the factory {descriptor.ImplementationFactory!.Method.DeclaringType?.FullName}.{descriptor.ImplementationFactory.Method.Name}"
                ),
        ];
        return registered.Count switch
        {
            1 => [],
            0 => ["The Host registers no IFileStorage provider, and composes exactly one: AddPouryaKitFileStorage registered one, then it was removed."],
            _ =>
            [
                $"The Host registers {registered.Count} IFileStorage providers, {string.Join(", ", registered)}, and composes exactly one: remove all but one of its file storage registrations.",
            ],
        };
    }
}
