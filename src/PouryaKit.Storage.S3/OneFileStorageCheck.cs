using PouryaKit.Cqrs;

namespace PouryaKit.Storage.S3;

// A Host composes exactly one IFileStorage, which its final registrations show whatever their order: with two, the
// container would serve the last silently. One problem naming each registration's type, or the factory that builds it.
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
        return registered.Count < 2
            ? []
            : [
                $"The Host registers {registered.Count} IFileStorage providers, {string.Join(", ", registered)}, and composes exactly one: remove all but one of its file storage registrations.",
            ];
    }
}
