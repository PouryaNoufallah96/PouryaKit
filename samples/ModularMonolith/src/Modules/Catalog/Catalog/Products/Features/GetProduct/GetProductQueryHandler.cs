using Catalog.Products.Errors;
using Catalog.Products.ValueObjects;
using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;

namespace Catalog.Products.Features.GetProduct;

#region get-product-dapper-read
// Plain SQL reads no query filter, so it compares the tenant the lease bound itself, as it names is_deleted.
internal sealed class GetProductQueryHandler(IReadConnectionFactory connections)
    : QueryHandler<GetProductQuery, ProductDto>
{
    public override async Task<Result<ProductDto>> Handle(
        GetProductQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var product = await connection.QuerySingleOrDefaultAsync<ProductDto>(
            new CommandDefinition(
                "select id, category_id as categoryid, name from catalog.products where id = @Id and tenant = current_setting('pouryakit.tenant') and is_deleted = false",
                new { Id = new ProductId(query.ProductId) },
                cancellationToken: cancellationToken
            )
        );
        return product is null ? ProductErrors.NotFound : product;
    }
}
#endregion
