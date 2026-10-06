namespace PouryaKit.Cqrs.Tests;

public sealed class PageTests
{
    private static readonly PageDeclaration Declaration = new(
        sorts: ["name"],
        filters: [FilterField.Of<Guid>("category", FilterOperator.Equal), FilterField.Of<DateTimeOffset>("created", FilterOperator.GreaterThanOrEqual)],
        maxSize: 50
    );

    [Theory]
    [InlineData(1, 10, 0, 0, false, false)]
    [InlineData(1, 2, 5, 3, true, false)]
    [InlineData(2, 2, 5, 3, true, true)]
    [InlineData(3, 2, 5, 3, false, true)]
    [InlineData(4, 2, 4, 2, false, true)]
    public void A_pages_navigation_is_derived_from_its_number_size_and_total(
        int number,
        int size,
        long total,
        long totalPages,
        bool hasNext,
        bool hasPrevious
    )
    {
        var page = new Page<string>([], number, size, total);

        Assert.Equal((totalPages, hasNext, hasPrevious), (page.TotalPages, page.HasNext, page.HasPrevious));
    }

    [Fact]
    public void A_number_whose_offset_does_not_fit_an_int_is_out_of_range_and_the_last_reachable_one_is_not()
    {
        var reachable = int.MaxValue / 50 + 1;

        var beyond = Declaration.Validate(new PageRequest { Number = reachable + 1, Size = 50 });

        Assert.True(Declaration.Validate(new PageRequest { Number = reachable, Size = 50 }).IsSuccess);
        Assert.Equal("Page.NumberOutOfRange", Assert.Single(beyond.Errors).Code);
    }

    [Fact]
    public void A_filter_value_is_read_as_its_declared_type_from_that_type_or_its_text()
    {
        var category = Guid.CreateVersion7();
        PageFilter[] filters =
        [
            new("category", FilterOperator.Equal, category),
            new("category", FilterOperator.Equal, category.ToString()),
            new("created", FilterOperator.GreaterThanOrEqual, "2026-10-07T12:00:00"),
        ];

        Assert.Equal(
            [category, category, new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero)],
            filters.Select(Declaration.ValueOf)
        );
        Assert.Throws<InvalidOperationException>(() => Declaration.ValueOf(new("category", FilterOperator.Equal, 42)));
    }

    [Fact]
    public void A_declaration_refuses_a_name_that_is_not_plain_letters_and_digits_and_an_operator_its_type_cannot_use()
    {
        Assert.Throws<ArgumentException>(() => new PageDeclaration(["name desc"]));
        Assert.Throws<ArgumentException>(() => new PageDeclaration(["name"], [FilterField.Of<string>("na-me", FilterOperator.Equal)]));
        Assert.Throws<ArgumentException>(() => FilterField.Of<Guid>("category", FilterOperator.Contains));
        Assert.Throws<ArgumentException>(() => FilterField.Of<string>("name", FilterOperator.GreaterThan));
        Assert.Throws<ArgumentException>(() => FilterField.Of<float>("weight", FilterOperator.Equal));
    }

    [Fact]
    public void Each_operator_has_one_short_name_that_parses_back_to_it()
    {
        Assert.Equal(
            ["eq", "ne", "contains", "startswith", "gt", "gte", "lt", "lte"],
            Enum.GetValues<FilterOperator>().Select(filterOperator => filterOperator.Name)
        );
        Assert.All(
            Enum.GetValues<FilterOperator>(),
            filterOperator =>
                Assert.True(FilterOperator.TryParseName(filterOperator.Name, out var parsed) && parsed == filterOperator)
        );
        Assert.False(FilterOperator.TryParseName("Equal", out _));
    }
}
