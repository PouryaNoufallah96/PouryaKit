namespace PouryaKit.Cqrs;

// A query whose handler is registered and that asks the query cache to answer it (IRequiresQueryCache), in a Host that
// registers no IQueryCache: the dispatcher would run it uncached on every call, which is never silent (ADR 0002).
internal sealed class QueryCacheCheck : ICompositionCheck
{
    public IEnumerable<string> Check(HostComposition composition, IServiceProvider services)
    {
        var unkeyed = composition
            .Registrations.Where(descriptor => !descriptor.IsKeyedService)
            .ToList();
        List<Type> cacheable =
        [
            .. unkeyed
                .Select(descriptor => OperationTypes.HandledBy(descriptor.ServiceType))
                .OfType<Type>()
                .Where(operation => operation.IsAssignableTo(typeof(IRequiresQueryCache)))
                .Distinct(),
        ];
        return
            cacheable.Count == 0
            || unkeyed.Any(descriptor => descriptor.ServiceType == typeof(IQueryCache))
            ? []
            :
            [
                $"The Host registers no query cache, so every cacheable query would run uncached: {string.Join(", ", cacheable.Select(query => query.FullName))}. Call AddPouryaKitCaching() on the Host's services.",
            ];
    }
}
