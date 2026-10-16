using FluentValidation;
using PouryaKit.Cqrs;

namespace PouryaKit.Validation.Tests;

// A plain record, not a query, so the assembly scan of ValidationTests expects no handler for it.
internal sealed record PagedLamps(PageRequest Page)
{
    public static readonly PageDeclaration Paging = new(
        sorts: ["name"],
        filters: [FilterField.Of<int>("watts", FilterOperator.Equal)],
        maxSize: 50
    );
}

internal sealed class PagedLampsValidator : AbstractValidator<PagedLamps>
{
    public PagedLampsValidator() => RuleFor(lamps => lamps.Page).NotNull().AllowedBy(PagedLamps.Paging);
}

public sealed class PageRuleTests
{
    [Fact]
    public void Each_error_of_the_declaration_comes_back_as_a_failure_with_its_message_target_and_code()
    {
        var page = new PageRequest
        {
            Number = 0,
            Size = 51,
            Sort = "price",
            Filters = [new PageFilter("watts", FilterOperator.Equal, "bright")],
        };

        var failures = new PagedLampsValidator().Validate(new PagedLamps(page)).Errors;

        var expected = PagedLamps.Paging.Validate(page).Errors;
        Assert.Equal(
            [PageErrorCodes.SizeOutOfRange, PageErrorCodes.NumberOutOfRange, PageErrorCodes.SortUnknown, PageErrorCodes.FilterValueInvalid],
            expected.Select(error => error.Code)
        );
        Assert.Equal(
            expected.Select(error => (error.Target, error.Message, error.Code)),
            failures.Select(failure => ((string?)failure.PropertyName, failure.ErrorMessage, failure.ErrorCode))
        );
    }

    [Fact]
    public void A_request_inside_the_declaration_has_no_failure()
    {
        var page = new PageRequest { Sort = "name", Filters = [new PageFilter("watts", FilterOperator.Equal, "40")] };

        Assert.Empty(new PagedLampsValidator().Validate(new PagedLamps(page)).Errors);
    }

    [Fact]
    public void A_null_request_fails_only_the_validators_own_not_null_rule()
    {
        var failure = Assert.Single(new PagedLampsValidator().Validate(new PagedLamps(null!)).Errors);

        Assert.Equal(("Page", "NotNullValidator"), (failure.PropertyName, failure.ErrorCode));
    }
}
