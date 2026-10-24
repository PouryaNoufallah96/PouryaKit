using Catalog.Products.ValueObjects;
using PouryaKit.Domain;

namespace Catalog.Products.Entities;

// One Actor's conversation about the catalog's products in one tenant, found by its key: the session the agent saved
// and what its memory keeps, each as JSON the domain never reads.
internal sealed class ProductConversation : AggregateRoot<ProductConversationId>, ITenantScoped
{
    private ProductConversation() { }

    public string Tenant { get; private set; } = "";

    public string Key { get; private set; } = "";

    public string? Session { get; private set; }

    public string? Memory { get; private set; }

    public static ProductConversation Start(ProductConversationId id, string key, string tenant)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        return new ProductConversation { Id = id, Key = key, Tenant = tenant };
    }

    public void Keep(string session) => Session = session;

    public void Remember(string memory) => Memory = memory;
}
