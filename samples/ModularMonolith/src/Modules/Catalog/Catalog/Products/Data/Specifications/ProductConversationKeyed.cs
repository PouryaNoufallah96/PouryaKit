using Ardalis.Specification;
using Catalog.Products.Entities;

namespace Catalog.Products.Data.Specifications;

internal sealed class ProductConversationKeyed : Specification<ProductConversation>
{
    public ProductConversationKeyed(string key) =>
        Query.Where(conversation => conversation.Key == key);
}
