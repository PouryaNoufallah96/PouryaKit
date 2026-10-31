namespace PouryaKit.Cqrs;

// A command or query whose handler is registered and that declares, with an IRequiresAuthorizer attribute such as
// Security's [RequiresPermission], an authorizer of the Kit that is not registered for it: its check would never run.
// Another authorizer of the same operation does not count. One problem for each such declaration.
internal sealed class DeclaredAuthorizersCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        // A keyed descriptor's ImplementationType is null, and the dispatcher resolves handlers and authorizers unkeyed.
        var unkeyed = composition
            .Registrations.Where(descriptor => !descriptor.IsKeyedService)
            .ToList();
        var registered = unkeyed
            .Where(descriptor => descriptor.ImplementationType is not null)
            .Select(descriptor => (descriptor.ServiceType, descriptor.ImplementationType!))
            .ToHashSet();
        return unkeyed
            .Select(descriptor => OperationTypes.HandledBy(descriptor.ServiceType))
            .OfType<Type>()
            .Distinct()
            .SelectMany(operation =>
                operation
                    .GetCustomAttributes(inherit: false)
                    .OfType<IRequiresAuthorizer>()
                    .Where(declared =>
                        !registered.Contains(
                            (
                                typeof(IAuthorizer<>).MakeGenericType(operation),
                                declared.AuthorizerOf(operation)
                            )
                        )
                    )
                    .Select(declared =>
                        $"{operation.FullName} declares {declared.GetType().Name}, but its check is not registered, so it would run unchecked. Call {declared.Registration} with the assembly {operation.Assembly.GetName().Name} in the Composition Root that registers its handler."
                    )
            );
    }
}
