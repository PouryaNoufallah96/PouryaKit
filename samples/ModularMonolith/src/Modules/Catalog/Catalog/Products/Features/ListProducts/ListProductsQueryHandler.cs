using Dapper;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;

namespace Catalog.Products.Features.ListProducts;

internal sealed class ListProductsQueryHandler(IReadConnectionFactory connections)
    : QueryHandler<ListProductsQuery, List<ProductListItemDto>>
{
    public override async Task<Result<List<ProductListItemDto>>> Handle(
        ListProductsQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        var products = await connection.QueryAsync<ProductListItemDto>(
            new CommandDefinition(
                "select id, category_id as categoryid, name from catalog.products where is_deleted = false order by name, id",
                cancellationToken: cancellationToken
            )
        );
        return products.AsList();
    }
}
