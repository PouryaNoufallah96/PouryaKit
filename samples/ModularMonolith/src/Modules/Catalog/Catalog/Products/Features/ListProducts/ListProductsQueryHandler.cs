using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;

namespace Catalog.Products.Features.ListProducts;

internal sealed class ListProductsQueryHandler(IReadConnectionFactory connections)
    : QueryHandler<ListProductsQuery, Page<ProductListItemDto>>
{
    // The Feature writes its WHERE; each declared filter arrives as @{field}_{operator}, null when not asked for.
    private const string From = """
        from catalog.products p
        where not p.is_deleted
          and (@name_eq is null or p.name = @name_eq)
          and (@name_contains is null or strpos(p.name, @name_contains) > 0)
          and (@category_eq is null or p.category_id = @category_eq)
          and (@created_gte is null or p.created_at >= @created_gte)
        """;

    private static readonly PageSql Sql = new PageSql(
        ListProductsQuery.Paging,
        select: $"select p.id, p.category_id as categoryid, p.name {From}",
        count: $"select count(*) {From}"
    )
        .OrderBy("name", "p.name")
        .OrderBy("created", "p.created_at")
        .ThenBy("p.id");

    public override async Task<Result<Page<ProductListItemDto>>> Handle(
        ListProductsQuery query,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await connection.QueryPageAsync<ProductListItemDto>(Sql, query.Page, cancellationToken: cancellationToken);
    }
}
