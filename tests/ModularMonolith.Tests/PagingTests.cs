using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Catalog;
using Catalog.Categories.Features.CreateCategory;
using Catalog.Products.Features.CreateProduct;
using Catalog.Products.Features.ListProducts;
using Microsoft.Extensions.DependencyInjection;
using ModularMonolith.Tests.Stub;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Testing;
using static ModularMonolith.Tests.Fixtures.ProblemResponses;

namespace ModularMonolith.Tests;

// ListProducts pages through Dapper; ProductPages reads the same Page through EF Core under the same declaration.
public sealed class PagingTests(AppFixture app) : HostTest<AppFixture>(app)
{
    // Created in this order, so "created" sorts them so. Contains is case-sensitive: "lamp" leaves "Lamp shade" out.
    private static readonly (string Name, bool Lighting)[] Products =
    [
        ("Desk lamp", true),
        ("Oak desk", false),
        ("Floor lamp", true),
        ("Armchair", false),
        ("Lamp shade", true),
    ];

    [Theory]
    [InlineData("by created")]
    [InlineData("by name, second page")]
    [InlineData("by name descending")]
    [InlineData("lighting, newest first")]
    [InlineData("lighting, newest first, last page")]
    [InlineData("lighting, beyond the last page")]
    [InlineData("name contains")]
    [InlineData("name equals")]
    public async Task Dapper_and_ef_core_read_the_same_page_of_the_filtered_sorted_selection_with_its_total(
        string @case
    )
    {
        var (lighting, _) = await SeedAsync();
        var (request, names, total) = @case switch
        {
            "by created" => (
                new PageRequest { Sort = "created", Size = 5 },
                Products.Select(product => product.Name).ToArray(),
                5
            ),
            "by name, second page" => (
                new PageRequest
                {
                    Sort = "name",
                    Number = 2,
                    Size = 2,
                },
                ["Floor lamp", "Lamp shade"],
                5
            ),
            "by name descending" => (
                new PageRequest
                {
                    Sort = "name",
                    Direction = SortDirection.Descending,
                    Size = 2,
                },
                ["Oak desk", "Lamp shade"],
                5
            ),
            "lighting, newest first" => (
                Lighting(lighting, number: 1),
                ["Lamp shade", "Floor lamp"],
                3
            ),
            "lighting, newest first, last page" => (
                Lighting(lighting, number: 2),
                ["Desk lamp"],
                3
            ),
            "lighting, beyond the last page" => (Lighting(lighting, number: 3), [], 3),
            "name contains" => (
                new PageRequest { Filters = [new("name", FilterOperator.Contains, "lamp")] },
                ["Desk lamp", "Floor lamp"],
                2
            ),
            "name equals" => (
                new PageRequest { Filters = [new("name", FilterOperator.Equal, "Oak desk")] },
                ["Oak desk"],
                1
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        var dapper = await ListAsync(request);
        var ef = await PageThroughEfAsync(request);

        foreach (var page in new[] { dapper, ef })
        {
            Assert.Equal(names, page.Value.Items.Select(item => item.Name));
            Assert.Equal(
                (request.Number, request.Size, (long)total),
                (page.Value.Number, page.Value.Size, page.Value.Total)
            );
        }
    }

    [Fact]
    public async Task A_request_outside_the_declaration_fails_validation_listing_every_reason_and_reads_nothing()
    {
        await SeedAsync();

        var listed = await ListAsync(
            new PageRequest
            {
                Number = 0,
                Size = 51,
                Sort = "price",
                Filters =
                [
                    new("category", FilterOperator.Contains, "lamp"),
                    new("category", FilterOperator.Equal, 42),
                    new("category", FilterOperator.Equal, "not a uuid"),
                    new("colour", FilterOperator.Equal, "red"),
                    new("name", FilterOperator.Equal, "Oak desk"),
                    new("name", FilterOperator.Equal, "Armchair"),
                ],
            }
        );

        Assert.All(listed.Errors, error => Assert.Equal(ErrorKind.Validation, error.Kind));
        Assert.Equal(
            [
                (PageErrorCodes.SizeOutOfRange, "Size"),
                (PageErrorCodes.NumberOutOfRange, "Number"),
                (PageErrorCodes.SortUnknown, "Sort"),
                (PageErrorCodes.FilterOperatorUnsupported, "Filters[0]"),
                // A typed value of another type is refused as text that does not parse is.
                (PageErrorCodes.FilterValueInvalid, "Filters[1]"),
                (PageErrorCodes.FilterValueInvalid, "Filters[2]"),
                (PageErrorCodes.FilterUnknown, "Filters[3]"),
                (PageErrorCodes.FilterRepeated, "Filters[5]"),
            ],
            listed.Errors.Select(error => (error.Code, error.Target))
        );
    }

    [Theory]
    [InlineData(FilterOperator.Equal, "Oak desk' or '1'='1")]
    [InlineData(FilterOperator.Equal, "x'; delete from catalog.products; --")]
    [InlineData(FilterOperator.Contains, "') > 0 or (1 = 1")]
    public async Task Sql_in_a_filter_value_stays_a_value_that_no_product_has_through_dapper_and_ef_core(
        FilterOperator filterOperator,
        string value
    )
    {
        await SeedAsync();
        var request = new PageRequest { Filters = [new("name", filterOperator, value)] };

        var dapper = await ListAsync(request);
        var ef = await PageThroughEfAsync(request);

        foreach (var page in new[] { dapper, ef })
        {
            Assert.Empty(page.Value.Items);
            Assert.Equal(0, page.Value.Total);
        }

        Assert.Equal(5L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Fact]
    public async Task Sql_as_a_sort_key_is_a_validation_error_and_a_helper_given_it_unvalidated_throws_before_any_sql_runs()
    {
        await SeedAsync();
        var request = new PageRequest { Sort = "name; delete from catalog.products" };

        var listed = await ListAsync(request);
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        await using var lease = await scope
            .ServiceProvider.GetRequiredService<IReadConnectionFactory>()
            .OpenAsync(TestContext.Current.CancellationToken);
        var dapper = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lease.QueryPageAsync<object>(
                new PageSql(
                    ListProductsQuery.Paging,
                    "select p.name from catalog.products p where @name_eq is null and @name_contains is null and @category_eq is null and @created_gte is null",
                    "select count(*) from catalog.products p where @name_eq is null and @name_contains is null and @category_eq is null and @created_gte is null"
                )
                    .OrderBy("name", "p.name")
                    .OrderBy("created", "p.created_at")
                    .ThenBy("p.id"),
                request,
                cancellationToken: TestContext.Current.CancellationToken
            )
        );
        var ef = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PageThroughEfAsync(request)
        );

        Assert.Equal(
            (PageErrorCodes.SortUnknown, "Sort"),
            (Assert.Single(listed.Errors).Code, listed.Errors[0].Target)
        );
        Assert.Contains(PageErrorCodes.SortUnknown, dapper.Message);
        Assert.Contains(PageErrorCodes.SortUnknown, ef.Message);
        Assert.Equal(5L, await Fixture.ScalarAsync<long>("select count(*) from catalog.products"));
    }

    [Fact]
    public async Task A_time_filter_with_any_offset_selects_from_the_same_instant_through_dapper_and_ef_core()
    {
        await SeedAsync();
        var created = await Fixture.ScalarAsync<DateTime>(
            "select created_at from catalog.products where name = 'Floor lamp'"
        );
        // The same instant as the third product's creation, written in Tehran's offset rather than UTC.
        var since = new DateTimeOffset(created, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(3.5));
        var request = new PageRequest
        {
            Sort = "created",
            Filters = [new("created", FilterOperator.GreaterThanOrEqual, since)],
        };

        var dapper = await ListAsync(request);
        var ef = await PageThroughEfAsync(request);

        foreach (var page in new[] { dapper, ef })
        {
            Assert.Equal(
                ["Floor lamp", "Armchair", "Lamp shade"],
                page.Value.Items.Select(item => item.Name)
            );
            Assert.Equal(3, page.Value.Total);
        }
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(null, 0)]
    [InlineData(null, 2)]
    public async Task An_ef_core_page_refuses_a_specification_that_sets_its_own_skip_or_take(
        int? skip,
        int? take
    )
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(
            new OperationContext(GatewayTestExtensions.TestActor, AppFixture.TestTenant),
            typeof(CatalogModule)
        );
        var pages = new ProductPages(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() =>
            pages.PageLimitedAsync(skip, take, TestContext.Current.CancellationToken)
        );

        Assert.Equal("specification", thrown.ParamName);
    }

    [Fact]
    public void A_page_sql_whose_count_leaves_out_a_declared_filter_is_refused()
    {
        var thrown = Assert.Throws<ArgumentException>(() =>
            new PageSql(
                ListProductsQuery.Paging,
                "select p.name from catalog.products p where @name_eq is null and @name_contains is null and @category_eq is null and @created_gte is null",
                "select count(*) from catalog.products p where @name_eq is null and @name_contains is null and @created_gte is null"
            )
        );

        Assert.Equal("count", thrown.ParamName);
        Assert.Contains("@category_eq", thrown.Message);
    }

    // FilterField keeps its own list of value types, so one it gains that the Dapper helper cannot send fails when the
    // Feature's SQL is built, not at the first request that filters on it. FilterField.Of refuses every type outside its
    // list today, so the field is made as such a release would make it.
    [Fact]
    public void A_page_sql_whose_filter_has_a_value_type_it_has_no_database_type_for_is_refused_when_built()
    {
        var field = (FilterField)RuntimeHelpers.GetUninitializedObject(typeof(FilterField));
        foreach (
            var (property, value) in new (string, object)[]
            {
                (nameof(FilterField.Name), "day"),
                (nameof(FilterField.ValueType), typeof(DateOnly)),
                (
                    nameof(FilterField.Operators),
                    (IReadOnlyList<FilterOperator>)[FilterOperator.Equal]
                ),
            }
        )
        {
            typeof(FilterField)
                .GetField(
                    $"<{property}>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic
                )!
                .SetValue(field, value);
        }

        var thrown = Assert.Throws<ArgumentException>(() =>
            new PageSql(
                new PageDeclaration(["day"], [field]),
                "select d.day from days d where @day_eq is null",
                "select count(*) from days d where @day_eq is null"
            )
        );

        Assert.Equal("declaration", thrown.ParamName);
        Assert.StartsWith(
            "The filter field day has values of DateOnly, which a Dapper Page read has no database type for.",
            thrown.Message,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task The_http_page_is_read_from_the_query_string_and_answered_as_the_page_object()
    {
        var (lighting, ids) = await SeedAsync();
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

        var response = await client.GetAsync(
            $"/catalog/products?page=1&size=2&sort=created&direction=desc&filter[category][eq]={lighting}",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $$"""{"items":[{"id":"{{ids["Lamp shade"]}}","categoryId":"{{lighting}}","name":"Lamp shade"},{"id":"{{ids["Floor lamp"]}}","categoryId":"{{lighting}}","name":"Floor lamp"}],"number":1,"size":2,"total":3,"totalPages":2,"hasNext":true,"hasPrevious":false}""",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
    }

    [Fact]
    public async Task Http_input_outside_the_declaration_answers_400_with_its_validation_errors()
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

        var response = await client.GetAsync(
            "/catalog/products?page=0&size=51&sort=name%3B%20delete%20from%20catalog.products&filter[category][contains]=x&filter[category][eq]=nope&filter[colour][eq]=red",
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(
            [
                (PageErrorCodes.FilterOperatorUnsupported, "Filters[0]"),
                (PageErrorCodes.FilterUnknown, "Filters[2]"),
                (PageErrorCodes.FilterValueInvalid, "Filters[1]"),
                (PageErrorCodes.NumberOutOfRange, "Number"),
                (PageErrorCodes.SizeOutOfRange, "Size"),
                (PageErrorCodes.SortUnknown, "Sort"),
            ],
            problem
                .GetProperty("errors")
                .EnumerateArray()
                .Select(error =>
                    (error.GetProperty("code").GetString(), error.GetProperty("target").GetString())
                )
                .Order()
        );
    }

    // The Kit's own reason is the problem's detail: it names the rule the query string broke.
    [Theory]
    [InlineData("page=two", "The page query parameter is a whole number.")]
    [InlineData("size=1.5", "The size query parameter is a whole number.")]
    [InlineData("page=1&page=2", "The page query parameter is given once.")]
    [InlineData("direction=up", "The direction query parameter is asc or desc.")]
    [InlineData(
        "filter[name][like]=lamp",
        "A filter query parameter is filter[field][operator], with an operator such as eq or contains."
    )]
    [InlineData(
        "filter[name]=lamp",
        "A filter query parameter is filter[field][operator], with an operator such as eq or contains."
    )]
    public async Task A_page_query_string_that_cannot_be_read_answers_400_as_a_problem_response(
        string query,
        string detail
    )
    {
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

        var response = await client.GetAsync(
            $"/catalog/products?{query}",
            TestContext.Current.CancellationToken
        );

        var problem = await ProblemAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(detail, problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("errors", out _));
    }

    // Query string keys are read ignoring case, as IQueryCollection reads page, size, sort and direction.
    [Fact]
    public async Task A_filter_key_whose_prefix_is_capitalised_filters_the_page()
    {
        var (lighting, _) = await SeedAsync();
        using var client = Fixture.Client("ada", AppFixture.TestTenant);

        var response = await client.GetAsync(
            $"/catalog/products?sort=created&Filter[category][eq]={lighting}",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var page = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal(
            ["Desk lamp", "Floor lamp", "Lamp shade"],
            page.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("name").GetString())
        );
        Assert.Equal(3, page.RootElement.GetProperty("total").GetInt64());
    }

    private static PageRequest Lighting(Guid lighting, int number) =>
        new()
        {
            Sort = "created",
            Direction = SortDirection.Descending,
            Number = number,
            Size = 2,
            Filters = [new("category", FilterOperator.Equal, lighting)],
        };

    private async Task<(Guid Lighting, Dictionary<string, Guid> Ids)> SeedAsync()
    {
        var catalog = Fixture.Gateway<CatalogModule>();
        var lighting = Guid.CreateVersion7();
        var furniture = Guid.CreateVersion7();
        await catalog.SendAsync(
            new CreateCategoryCommand(lighting, "Lighting"),
            TestContext.Current.CancellationToken
        );
        await catalog.SendAsync(
            new CreateCategoryCommand(furniture, "Furniture"),
            TestContext.Current.CancellationToken
        );
        Dictionary<string, Guid> ids = [];
        foreach (var (name, isLighting) in Products)
        {
            ids[name] = Guid.CreateVersion7();
            Assert.True(
                (
                    await catalog.SendAsync(
                        new CreateProductCommand(
                            ids[name],
                            isLighting ? lighting : furniture,
                            name
                        ),
                        TestContext.Current.CancellationToken
                    )
                ).IsSuccess
            );
        }

        return (lighting, ids);
    }

    private Task<Result<Page<ProductListItemDto>>> ListAsync(PageRequest request) =>
        Fixture
            .Gateway<CatalogModule>()
            .QueryAsync(new ListProductsQuery(request), TestContext.Current.CancellationToken);

    // The probe is test code, so no Gateway runs it; the test begins its scope as a Catalog Operation, as
    // RepositoryTests does.
    private async Task<Result<Page<ProductListItemDto>>> PageThroughEfAsync(PageRequest request)
    {
        await using var scope = Fixture.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(
            new OperationContext(GatewayTestExtensions.TestActor, AppFixture.TestTenant),
            typeof(CatalogModule)
        );
        return await scope
            .ServiceProvider.GetRequiredService<IDispatcher>()
            .QueryAsync(
                new PageProductsThroughEfQuery(request),
                TestContext.Current.CancellationToken
            );
    }
}
