using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Caching;
using PouryaKit.Cqrs;

// The packed Caching, registered as a Host would: it fills the dispatcher's query cache slot, and a query and a command
// declare what they cache and what they invalidate.
var services = new ServiceCollection().AddPouryaKitCqrs().AddPouryaKitCaching();
var filled = services.Any(descriptor => descriptor.ServiceType == typeof(IQueryCache) && descriptor.Lifetime == ServiceLifetime.Singleton);
ICacheableQuery query = new GetOrderQuery(Guid.CreateVersion7());
IInvalidatesCache command = new CancelOrderCommand(Guid.CreateVersion7());
Console.WriteLine(filled && query is IRequiresQueryCache && query.CacheTags.Count == 1 && command.InvalidatedCacheTags.Count == 1 ? 1 : 0);

internal sealed record GetOrderQuery(Guid OrderId) : Query<string>, ICacheableQuery
{
    public string CacheKey => OrderId.ToString();

    public TimeSpan CacheDuration => TimeSpan.FromMinutes(1);

    public IReadOnlyCollection<string> CacheTags => [$"order:{OrderId}"];
}

internal sealed record CancelOrderCommand(Guid OrderId) : Command, IInvalidatesCache
{
    public IReadOnlyCollection<string> InvalidatedCacheTags => [$"order:{OrderId}"];
}
