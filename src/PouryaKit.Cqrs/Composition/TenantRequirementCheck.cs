using PouryaKit.Core;

namespace PouryaKit.Cqrs;

// A tenant-scoped Module, or a tenant-scoped application, that dispatches a command or query without the tenant
// requirement: AddPouryaKitTenancy covers only the assemblies it was given, so an operation of one that AddPouryaKitCqrs
// scanned beside them would run without a tenant. Only the assemblies AddPouryaKitCqrs scanned for the scope count: one
// that only another registration named, such as AddPouryaKitLocalization, makes nothing dispatchable. One problem for each
// such operation, naming the scanned assembly that makes it dispatchable.
internal sealed class TenantRequirementCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        // A keyed descriptor's ImplementationType is null, and the dispatcher resolves authorizers unkeyed.
        var registered = composition
            .Registrations.Where(descriptor =>
                !descriptor.IsKeyedService && descriptor.ImplementationType is not null
            )
            .Select(descriptor => (descriptor.ServiceType, descriptor.ImplementationType!))
            .ToHashSet();
        Type?[] scopes = [null, .. composition.Modules];
        return scopes
            .Where(composition.IsTenantScoped)
            .SelectMany(scope =>
                composition
                    .DispatchingAssembliesOf(scope)
                    .SelectMany(assembly =>
                        OperationTypes
                            .Dispatchable(ImplementingTypes.Of(typeof(object), assembly))
                            .Select(operation => (Assembly: assembly, Operation: operation))
                    )
                    .DistinctBy(feature => feature.Operation)
                    .Where(feature =>
                        !registered.Contains(
                            (
                                typeof(IAuthorizer<>).MakeGenericType(feature.Operation),
                                typeof(TenantRequirement<>).MakeGenericType(feature.Operation)
                            )
                        )
                    )
                    .Select(feature =>
                        $"{scope?.Name ?? "The application"} is tenant-scoped, but it dispatches {feature.Operation.FullName} without the tenant requirement, so it would run without a tenant. Name its assembly {feature.Assembly.GetName().Name} to AddPouryaKitTenancy in {(scope is null ? "the Host's own services" : "its Composition Root")}."
                    )
            );
    }
}
