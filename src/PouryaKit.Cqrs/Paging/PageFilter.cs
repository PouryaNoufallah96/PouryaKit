namespace PouryaKit.Cqrs;

/// <summary>
/// One filter of a <see cref="PageRequest"/>: a declared field, one of its operators, and a value of its declared type
/// or text that parses to it, as HTTP gives it.
/// </summary>
public sealed record PageFilter(string Field, FilterOperator Operator, object Value);
