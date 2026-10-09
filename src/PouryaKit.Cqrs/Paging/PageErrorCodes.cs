namespace PouryaKit.Cqrs;

/// <summary>
/// The codes of the validation Errors <see cref="PageDeclaration.Validate(PageRequest)"/> returns, one for each thing
/// a <see cref="PageRequest"/> asks outside its declaration. Each Error's message names what the declaration allows.
/// </summary>
public static class PageErrorCodes
{
    /// <summary>The size is outside 1 to the declared maximum; the target is <c>Size</c>.</summary>
    public const string SizeOutOfRange = "Page.SizeOutOfRange";

    /// <summary>The number is below 1 or past the last Page an offset can reach; the target is <c>Number</c>.</summary>
    public const string NumberOutOfRange = "Page.NumberOutOfRange";

    /// <summary>The sort key is not declared; the target is <c>Sort</c>.</summary>
    public const string SortUnknown = "Page.SortUnknown";

    /// <summary>The direction is neither ascending nor descending; the target is <c>Direction</c>.</summary>
    public const string DirectionUnknown = "Page.DirectionUnknown";

    /// <summary>The filter's field is not declared; the target is the filter, <c>Filters[i]</c>.</summary>
    public const string FilterUnknown = "Page.FilterUnknown";

    /// <summary>The field does not declare the filter's operator; the target is the filter.</summary>
    public const string FilterOperatorUnsupported = "Page.FilterOperatorUnsupported";

    /// <summary>The filter's value is not of the field's type; the target is the filter.</summary>
    public const string FilterValueInvalid = "Page.FilterValueInvalid";

    /// <summary>A field and operator are given twice; the target is the second filter.</summary>
    public const string FilterRepeated = "Page.FilterRepeated";
}
