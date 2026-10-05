namespace PouryaKit.Domain;

/// <summary>
/// The base of a value object, declared as a record so that equality covers its type and every
/// member. A collection member compares by reference; wrap it in a value object of its own.
/// </summary>
public abstract record ValueObject
{
    protected static Result CheckRule(IBusinessRule rule) => BusinessRule.Check(rule);
}
