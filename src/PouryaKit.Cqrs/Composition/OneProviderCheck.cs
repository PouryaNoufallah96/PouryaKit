namespace PouryaKit.Cqrs;

/// <summary>
/// The composition check that a Host composes exactly one <typeparamref name="TService"/>, which its final registrations
/// show whatever their order: with two, the container would serve the last silently; with none, a removal after the
/// provider's registration, a consumer would fail only at resolution, or at build for a constructor-injected one. It
/// reports one problem for any other count of unkeyed registrations, naming each registration's type, or the factory that
/// builds it, or, for none, the contract and <paramref name="registration"/>. A provider package registers it for its
/// contract with <c>TryAddEnumerable</c>.
/// </summary>
/// <typeparam name="TService">The contract the Host composes exactly one provider of.</typeparam>
/// <param name="registration">The registration call that registers the provider, such as <c>AddPouryaKitFileStorage</c>.</param>
/// <param name="noun">The provider's kind, as the problem names its registrations, such as <c>file storage</c>.</param>
public sealed class OneProviderCheck<TService>(string registration, string noun) : ICompositionCheck
    where TService : class
{
    /// <inheritdoc />
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        // Keyed registrations are left out: a consumer resolves the contract unkeyed, and a keyed descriptor's
        // ImplementationType is null.
        List<string> registered =
        [
            .. composition
                .Registrations.Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(TService))
                .Select(descriptor =>
                    (descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType())?.FullName
                    ?? $"the factory {descriptor.ImplementationFactory!.Method.DeclaringType?.FullName}.{descriptor.ImplementationFactory.Method.Name}"
                ),
        ];
        var contract = typeof(TService).Name;
        return registered.Count switch
        {
            1 => [],
            0 => [$"The Host registers no {contract} provider, and composes exactly one: {registration} registered one, then it was removed."],
            _ =>
            [
                $"The Host registers {registered.Count} {contract} providers, {string.Join(", ", registered)}, and composes exactly one: remove all but one of its {noun} registrations.",
            ],
        };
    }
}
