using Ardalis.Specification;
using Catalog.Categories.ValueObjects;
using Catalog.Products.Entities;
using Catalog.Products.Features.ListProducts;
using Catalog.Products.ValueObjects;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests.Stub;

// Reads the Page ListProducts reads through Dapper, through EF Core and Catalog's DbContext instead, under the same
// declaration. Run in a scope the test begins as a Catalog Operation, as ProbeProductRepositoriesCommand is.
internal sealed record PageProductsThroughEfQuery(PageRequest Page)
    : Query<Page<ProductListItemDto>>;

internal sealed class PageProductsThroughEfQueryHandler(IUnitOfWork unitOfWork)
    : QueryHandler<PageProductsThroughEfQuery, Page<ProductListItemDto>>
{
    public override async Task<Result<Page<ProductListItemDto>>> Handle(
        PageProductsThroughEfQuery query,
        CancellationToken cancellationToken
    ) => await new ProductPages(unitOfWork).PageAsync(query.Page, cancellationToken);
}

internal sealed class ProductPages(IUnitOfWork unitOfWork)
    : EfRepository<Product, ProductId>(unitOfWork)
{
    public static readonly PageExpressions<Product> Paging = new PageExpressions<Product>(
        ListProductsQuery.Paging
    )
        .OrderBy("name", product => product.Name)
        .OrderBy("created", product => product.CreatedAt)
        .ThenBy(product => product.Id)
        .Filter<string>("name", FilterOperator.Equal, name => product => product.Name == name)
        .Filter<string>(
            "name",
            FilterOperator.Contains,
            name => product => product.Name.Contains(name)
        )
        .Filter<Guid>(
            "category",
            FilterOperator.Equal,
            id => product => product.CategoryId == new CategoryId(id)
        )
        .Filter<DateTimeOffset>(
            "created",
            FilterOperator.GreaterThanOrEqual,
            since => product => product.CreatedAt >= since
        );

    public Task<Page<ProductListItemDto>> PageAsync(
        PageRequest request,
        CancellationToken cancellationToken
    ) => PageAsync(new ListItems(), Paging, request, cancellationToken);

    // The same read with a specification that also sets its own Skip or Take, which the Page must refuse.
    public Task<Page<ProductListItemDto>> PageLimitedAsync(
        int? skip,
        int? take,
        CancellationToken cancellationToken
    ) => PageAsync(new ListItems(skip, take), Paging, new PageRequest(), cancellationToken);

    private sealed class ListItems : Specification<Product, ProductListItemDto>
    {
        public ListItems(int? skip = null, int? take = null)
        {
            Query.Select(product => new ProductListItemDto(
                product.Id,
                product.CategoryId,
                product.Name
            ));
            if (skip is { } skipped)
            {
                Query.Skip(skipped);
            }

            if (take is { } taken)
            {
                Query.Take(taken);
            }
        }
    }
}
