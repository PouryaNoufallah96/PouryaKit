using Catalog.Contracts.Products.Events.V1;
using Catalog.Products.Entities;
using PouryaKit.Messaging;

namespace Catalog.Products.Events;

#region product-snapshot-mapper
// The product as other Modules see it. The Kit calls it once for every product an Operation changed, after every
// reaction ran and the version is the one being committed; it only reads.
internal sealed class ProductSnapshotMapper : ISnapshotMapper<Product, ProductSnapshotV1>
{
    public ProductSnapshotV1 Map(Product product) => new(product.Id.Value, product.Name, IsRemoved: false, product.Version);

    public ProductSnapshotV1 Tombstone(Product product) => new(product.Id.Value, product.Name, IsRemoved: true, product.Version);
}
#endregion
