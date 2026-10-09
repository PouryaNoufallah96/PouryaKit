namespace PouryaKit.Cqrs.Tests;

// The codes are published: a consumer and its clients compare them as text. The literals here pin each symbol's
// value, so a changed symbol fails this test while every other test names the symbol.
public sealed class ErrorCodeTests
{
    [Fact]
    public void The_operation_and_page_error_codes_keep_their_published_text()
    {
        Assert.Equal(
            ["Operation.CommandThrew", "Operation.ReadingChangedState"],
            [OperationErrors.CommandThrew.Code, OperationErrors.ReadingChangedState.Code]
        );
        Assert.Equal(
            [
                "Page.SizeOutOfRange",
                "Page.NumberOutOfRange",
                "Page.SortUnknown",
                "Page.DirectionUnknown",
                "Page.FilterUnknown",
                "Page.FilterOperatorUnsupported",
                "Page.FilterValueInvalid",
                "Page.FilterRepeated",
            ],
            [
                PageErrorCodes.SizeOutOfRange,
                PageErrorCodes.NumberOutOfRange,
                PageErrorCodes.SortUnknown,
                PageErrorCodes.DirectionUnknown,
                PageErrorCodes.FilterUnknown,
                PageErrorCodes.FilterOperatorUnsupported,
                PageErrorCodes.FilterValueInvalid,
                PageErrorCodes.FilterRepeated,
            ]
        );
    }
}
