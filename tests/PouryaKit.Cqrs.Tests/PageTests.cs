namespace PouryaKit.Cqrs.Tests;

public sealed class PageTests
{
    private static readonly PageDeclaration Declaration = new(
        sorts: ["name"],
        filters:
        [
            FilterField.Of<Guid>("category", FilterOperator.Equal),
            FilterField.Of<DateTimeOffset>("created", FilterOperator.GreaterThanOrEqual),
        ],
        maxSize: 50
    );

    [Theory]
    [InlineData(1, 10, 0, 0, false, false)]
    [InlineData(1, 2, 5, 3, true, false)]
    [InlineData(2, 2, 5, 3, true, true)]
    [InlineData(3, 2, 5, 3, false, true)]
    [InlineData(4, 2, 4, 2, false, true)]
    [InlineData(1, 2, long.MaxValue, long.MaxValue / 2 + 1, true, false)]
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

        Assert.Equal(
            (totalPages, hasNext, hasPrevious),
            (page.TotalPages, page.HasNext, page.HasPrevious)
        );
    }

    [Fact]
    public void A_number_whose_offset_does_not_fit_an_int_is_out_of_range_and_the_last_reachable_one_is_not()
    {
        var reachable = int.MaxValue / 50 + 1;

        var beyond = Declaration.Validate(new PageRequest { Number = reachable + 1, Size = 50 });

        Assert.True(
            Declaration.Validate(new PageRequest { Number = reachable, Size = 50 }).IsSuccess
        );
        Assert.Equal(PageErrorCodes.NumberOutOfRange, Assert.Single(beyond.Errors).Code);
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
            new("created", FilterOperator.GreaterThanOrEqual, "2026-10-07T15:30:00+03:30"),
            new(
                "created",
                FilterOperator.GreaterThanOrEqual,
                new DateTimeOffset(2026, 10, 7, 15, 30, 0, TimeSpan.FromHours(3.5))
            ),
        ];
        var noon = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        var values = filters.Select(Declaration.ValueOf).ToList();

        Assert.Equal([category, category, noon, noon, noon], values);
        // Every time is read as UTC, whatever offset it was given in, so both helpers send the same value.
        Assert.All(
            values.OfType<DateTimeOffset>(),
            time => Assert.Equal(TimeSpan.Zero, time.Offset)
        );
        Assert.Throws<InvalidOperationException>(() =>
            Declaration.ValueOf(new("category", FilterOperator.Equal, 42))
        );
    }

    [Fact]
    public void A_declaration_refuses_a_name_that_is_not_plain_letters_and_digits_and_an_operator_its_type_cannot_use()
    {
        Assert.Throws<ArgumentException>(() => new PageDeclaration(["name desc"]));
        Assert.Throws<ArgumentException>(() =>
            new PageDeclaration(["name"], [FilterField.Of<string>("na-me", FilterOperator.Equal)])
        );
        Assert.Throws<ArgumentException>(() =>
            FilterField.Of<Guid>("category", FilterOperator.Contains)
        );
        Assert.Throws<ArgumentException>(() =>
            FilterField.Of<string>("name", FilterOperator.GreaterThan)
        );
        Assert.Throws<ArgumentException>(() =>
            FilterField.Of<float>("weight", FilterOperator.Equal)
        );
        // A maximum below the default size would refuse every request that states no size.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PageDeclaration(["name"], maxSize: PageRequest.DefaultSize - 1)
        );
        Assert.True(
            new PageDeclaration(["name"], maxSize: PageRequest.DefaultSize)
                .Validate(new PageRequest())
                .IsSuccess
        );
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
                Assert.True(
                    FilterOperator.TryParseName(filterOperator.Name, out var parsed)
                        && parsed == filterOperator
                )
        );
        Assert.False(FilterOperator.TryParseName("Equal", out _));
        Assert.False(FilterOperator.TryParseName(null, out _));
    }
}
